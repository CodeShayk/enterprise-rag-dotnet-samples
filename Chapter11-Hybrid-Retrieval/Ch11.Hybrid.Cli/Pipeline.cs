using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Retrieval;

namespace Ch11.Hybrid.Cli;

/// <summary>
/// The hybrid retriever as a pipeline: what parallel fan-out saves, and what happens when one source is slow or fails.
/// </summary>
public static class Pipeline
{
    public static async Task RunAsync(Lab lab)
    {
        using var model = Lab.Bge();
        using var keyword = Commands.Keyword(lab);
        var dense = await lab.DenseAsync(model);
        var rrf = new ReciprocalRankFusion(60);
        var limit = TimeSpan.FromMilliseconds(200);
        var configurations = new (string Name, IRetriever Retriever)[]
        {
            ("keyword alone", keyword),
            ("dense alone", dense),
            ("both, one after the other", new Sequential([keyword, dense], rrf)),
            ("hybrid, in parallel", new HybridRetriever("hybrid", [keyword, dense], rrf, timeout: limit)),
            ("hybrid, dense 500 ms late", new HybridRetriever("hybrid", [keyword, new Slow(dense, 500)], rrf, timeout: limit)),
            ("hybrid, dense failing", new HybridRetriever("hybrid", [keyword, new Failing(dense)], rrf, timeout: limit)),
        };

        // Warm up both sources so the first question doesn't pay for JIT compilation and buffers.
        await new HybridRetriever("warm-up", [keyword, dense], rrf).RetrieveAsync(new RetrievalRequest("warm-up", People.Tomas, 4));

        Console.WriteLine($"Golden set v2, {lab.Split}: {lab.Answerable.Count()} questions, one at a time; time limit {limit.TotalMilliseconds:0} ms a source; CPU only");
        Console.WriteLine();
        Console.WriteLine($"{"Retriever",-28} {"p50 ms",7} {"p95 ms",7} {"Suff.",6}   Degraded");
        foreach (var (name, retriever) in configurations)
        {
            var latencies = new List<double>();
            var degraded = new Dictionary<string, int>();
            foreach (var question in lab.Answerable)
            {
                var stopwatch = Stopwatch.StartNew();
                var result = await retriever.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, 4));
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                foreach (var reason in result.Degraded) degraded[reason] = degraded.GetValueOrDefault(reason) + 1;
            }

            var run = await lab.RunAsync(retriever);
            latencies.Sort();
            Console.WriteLine($"{name,-28} {latencies[latencies.Count / 2],7:F1} {latencies[(int)(latencies.Count * 0.95)],7:F1} {run.Mean("Sufficient"),6:F2}   " +
                string.Join("; ", degraded.Select(d => $"{d.Value} × \"{d.Key}\"")));
        }
    }

    /// <summary>The same sources and fusion without the fan-out: each source waits for the one before it.</summary>
    private sealed class Sequential(IReadOnlyList<IRetriever> sources, IFusion fusion) : IRetriever
    {
        public string Name => "sequential";

        public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
        {
            var rankings = new List<IReadOnlyList<Hit>>();
            foreach (var source in sources) rankings.Add((await source.RetrieveAsync(request with { Top = 20 }, cancellationToken)).Hits);
            return RetrievalResult.Of(fusion.Fuse(rankings, request.Top));
        }
    }

    /// <summary>
    /// A source whose answer arrives late and that ignores cancellation, as a stuck dependency would. It computes
    /// first and waits afterwards: work the hybrid has stopped waiting for still runs, and the first version, which
    /// waited first, embedded with an ONNX session that had already been disposed and crashed the process.
    /// </summary>
    private sealed class Slow(IRetriever inner, int milliseconds) : IRetriever
    {
        public string Name => inner.Name;

        public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
        {
            var result = await inner.RetrieveAsync(request, CancellationToken.None);
            Thread.Sleep(milliseconds);
            return result;
        }
    }

    private sealed class Failing(IRetriever inner) : IRetriever
    {
        public string Name => inner.Name;

        public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("vector store unreachable");
    }
}
