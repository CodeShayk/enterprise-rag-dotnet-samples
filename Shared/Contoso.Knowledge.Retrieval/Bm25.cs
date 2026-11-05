using System.Text.RegularExpressions;
using Contoso.AI;

namespace Contoso.Knowledge.Retrieval;

/// <summary>
/// BM25 over an inverted index, from first principles (Chapter 2 built the index). k1 caps what repeating a term
/// can add to a chunk's score; b sets how much a chunk is penalized for being longer than average.
/// </summary>
public sealed class Bm25Index
{
    private readonly Dictionary<string, List<(int Chunk, int Tf)>> _postings = new(StringComparer.Ordinal);
    private readonly int[] _lengths;
    private readonly double _averageLength;

    public Bm25Index(IReadOnlyList<IReadOnlyList<string>> chunks, double k1 = 1.2, double b = 0.75)
    {
        (K1, B) = (k1, b);
        _lengths = new int[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            _lengths[i] = chunks[i].Count;
            foreach (var term in chunks[i].GroupBy(t => t, StringComparer.Ordinal))
            {
                if (!_postings.TryGetValue(term.Key, out var postings)) _postings[term.Key] = postings = [];
                postings.Add((i, term.Count()));
            }
        }

        _averageLength = _lengths.Average();
    }

    public double K1 { get; }

    public double B { get; }

    /// <summary>The score of every chunk that holds at least one query term; the caller filters and ranks.</summary>
    public Dictionary<int, double> Score(IEnumerable<string> query)
    {
        var scores = new Dictionary<int, double>();
        foreach (var term in query.Distinct(StringComparer.Ordinal))
        {
            if (!_postings.TryGetValue(term, out var postings)) continue;
            // Rare terms weigh more: a term in every chunk is worth almost nothing.
            var idf = Math.Log(1 + (_lengths.Length - postings.Count + 0.5) / (postings.Count + 0.5));
            foreach (var (chunk, tf) in postings)
            {
                // Saturation: tf * (k1 + 1) / (tf + k1 * norm) approaches k1 + 1 however often the term repeats.
                var norm = 1 - B + B * _lengths[chunk] / _averageLength;
                scores[chunk] = scores.GetValueOrDefault(chunk) + idf * tf * (K1 + 1) / (tf + K1 * norm);
            }
        }

        return scores;
    }
}

/// <summary>Chapter 5's analyzer: words with their inner hyphens and dots, stop words out, a light English stemmer.</summary>
public static partial class SimpleAnalyzer
{
    public static IEnumerable<string> Terms(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => !ContosoScripts.IsStopWord(w)).Select(Stem);

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

/// <summary>Keyword retrieval with <see cref="Bm25Index"/>: the reference that Chapters 9 and 10 compared against.</summary>
public sealed class Bm25Retriever : IRetriever
{
    private readonly IReadOnlyList<IndexedChunk> _chunks;
    private readonly Func<string, IEnumerable<string>> _analyze;
    private readonly Bm25Index _index;

    public Bm25Retriever(string name, IReadOnlyList<IndexedChunk> chunks, Func<string, IEnumerable<string>>? analyze = null, double k1 = 1.2, double b = 0.75)
    {
        (Name, _chunks, _analyze) = (name, chunks, analyze ?? SimpleAnalyzer.Terms);
        _index = new Bm25Index([.. chunks.Select(c => (IReadOnlyList<string>)[.. _analyze(c.Chunk.IndexText)])], k1, b);
    }

    public string Name { get; }

    public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var ranked = _index.Score(_analyze(request.Question))
            .Where(s => _chunks[s.Key].Passes(request.Asker, request.Filter))
            .OrderByDescending(s => s.Value).ThenBy(s => s.Key)
            .Select(s => new Hit(_chunks[s.Key], s.Value, Name));
        return Task.FromResult(RetrievalResult.Of(Ranking.DistinctReturns(ranked, request.Top)));
    }
}
