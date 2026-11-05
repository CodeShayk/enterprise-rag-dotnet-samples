using System.Text.RegularExpressions;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Evaluation;

namespace Ch10.Embeddings.Cli;

/// <summary>
/// Chapter 9's BM25 configuration, copied as the lexical reference for dense retrieval: BM25 over each chunk's index
/// text (header included), answering with each chunk's return span, a parent counted once.
/// </summary>
public sealed partial class Bm25Retrieval : IRetrievalUnderTest
{
    private readonly IReadOnlyList<Chunk> _chunks;
    private readonly IReadOnlyDictionary<string, SourceDocument> _documents;
    private readonly List<Dictionary<string, int>> _terms;
    private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    public Bm25Retrieval(string name, IReadOnlyList<Chunk> chunks, IReadOnlyDictionary<string, SourceDocument> documents)
    {
        Name = name;
        _chunks = chunks;
        _documents = documents;
        _terms = [.. chunks.Select(c => Terms(c.IndexText).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal))];
        foreach (var term in _terms.SelectMany(t => t.Keys)) _df[term] = _df.GetValueOrDefault(term) + 1;
        _averageLength = _terms.Average(t => t.Values.Sum());
        Chunks = [.. chunks.Select(c => new ChunkSpan(c.Doc, c.ReturnStart, c.ReturnEnd)).Distinct()];
        Parameters = new Dictionary<string, string> { ["chunks"] = chunks.Count.ToString(), ["retriever"] = "BM25 (k1 1.2, b 0.75)" };
    }

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    public IReadOnlyList<ChunkSpan> Chunks { get; }

    public Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var terms = Terms(question).Distinct().ToList();
        var ranked = Enumerable.Range(0, _chunks.Count)
            .Where(i => asker.CanRead(_documents[_chunks[i].Doc]))
            .Select(i => (i, score: Score(terms, i))).Where(r => r.score > 0).OrderByDescending(r => r.score);
        var results = new List<ChunkSpan>();
        foreach (var (i, _) in ranked)
        {
            var span = new ChunkSpan(_chunks[i].Doc, _chunks[i].ReturnStart, _chunks[i].ReturnEnd);
            if (!results.Contains(span)) results.Add(span);
            if (results.Count == top) break;
        }

        return Task.FromResult<IReadOnlyList<ChunkSpan>>(results);
    }

    private double Score(List<string> terms, int i)
    {
        const double k1 = 1.2, b = 0.75;
        var length = _terms[i].Values.Sum();
        double score = 0;
        foreach (var term in terms)
        {
            if (!_terms[i].TryGetValue(term, out var tf)) continue;
            var n = _df[term];
            score += Math.Log(1 + (_chunks.Count - n + 0.5) / (n + 0.5)) * tf * (k1 + 1) / (tf + k1 * (1 - b + b * length / _averageLength));
        }

        return score;
    }

    // Chapter 5's analyzer, copied because chapters don't reference each other.
    private static List<string> Terms(string text) =>
        [.. Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => !ContosoScripts.IsStopWord(w)).Select(Stem)];

    private static string Stem(string word) => word switch
    {
        _ when word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal) => word[..^3],
        _ when word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal) => word[..^3] + "y",
        _ when word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) => word[..^1],
        _ => word,
    };

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-.'][\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
