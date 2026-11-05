namespace Contoso.Knowledge.Retrieval;

/// <summary>
/// Runs several retrievers in parallel, each with its own time limit, and fuses whatever came back. A source that
/// fails or runs out of time is reported in <see cref="RetrievalResult.Degraded"/> and the search goes on without it.
/// </summary>
public sealed class HybridRetriever(string name, IReadOnlyList<IRetriever> sources, IFusion fusion, int candidates = 20, TimeSpan? timeout = null) : IRetriever
{
    public string Name { get; } = name;

    public TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(2);

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var runs = sources.Select(source => RunAsync(source, request with { Top = candidates }, cancellationToken));
        var results = await Task.WhenAll(runs);

        var answered = results.Where(r => r.Error is null).Select(r => r.Hits).ToList();
        var degraded = results.Where(r => r.Error is not null).Select(r => $"{r.Source}: {r.Error}").ToList();
        var hits = answered.Count switch
        {
            0 => [],
            1 => Ranking.DistinctReturns(answered[0], request.Top),
            _ => fusion.Fuse(answered, request.Top),
        };
        return new RetrievalResult(hits, degraded);
    }

    private async Task<(string Source, IReadOnlyList<Hit> Hits, string? Error)> RunAsync(IRetriever source, RetrievalRequest request, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        try
        {
            // Task.Run: Lucene and ONNX Runtime do their work synchronously, so without it the sources would run one
            // after the other. WaitAsync: a source that ignores its token can't hold the request past the limit.
            var result = await Task.Run(() => source.RetrieveAsync(request, limit.Token), limit.Token).WaitAsync(limit.Token);
            return (source.Name, result.Hits, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (source.Name, [], $"no answer within {Timeout.TotalMilliseconds:0} ms");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (source.Name, [], exception.GetType().Name);
        }
    }
}
