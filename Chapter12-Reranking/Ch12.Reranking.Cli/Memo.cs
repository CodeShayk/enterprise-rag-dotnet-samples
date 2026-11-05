using Contoso.Knowledge.Retrieval;

namespace Ch12.Reranking.Cli;

/// <summary>
/// Remembers a re-ranker's full ordering for each question and candidate list, so that the harness can score the
/// same rankings at k = 4 and k = 10 after one timed pass. The scores are the model's own; only repeats are skipped.
/// </summary>
public sealed class MemoReranker(IReranker inner) : IReranker
{
    private readonly Dictionary<string, IReadOnlyList<Hit>> _rankings = [];

    public string Name => inner.Name;

    public async Task<IReadOnlyList<Hit>> RerankAsync(string question, IReadOnlyList<Hit> candidates, int top, CancellationToken cancellationToken = default)
    {
        var key = question + "\n" + string.Join(";", candidates.Select(c => c.Chunk.ReturnKey));
        if (!_rankings.TryGetValue(key, out var ranked))
        {
            ranked = await inner.RerankAsync(question, candidates, candidates.Count, cancellationToken);
            _rankings[key] = ranked;
        }

        return [.. ranked.Take(top)];
    }
}
