using System.Text.RegularExpressions;
using Ch04.Knowledge.Core;
using Ch04.Knowledge.Core.Ports;
using Contoso.AI;
using Contoso.Corpus;

namespace Ch04.Knowledge.Adapters;

/// <summary>
/// Keyword retrieval with BM25 over chunks (Chapter 2's inverted index, adapted to the ports). It
/// skips chunks the asker may not read before it scores anything, which is the same contract the
/// vector index keeps with its filter. Chapter 11 replaces it with Lucene.NET.
/// </summary>
public sealed partial class KeywordIndex(double k1 = 1.2, double b = 0.75) : IIndexWriter, IRetriever
{
    // term -> (chunk ID -> how often the term occurs in that chunk)
    private readonly Dictionary<string, Dictionary<string, int>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Chunk Chunk, int Length)> _chunks = new(StringComparer.Ordinal);

    public Task WriteAsync(IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            var terms = Terms(chunk.Text);
            _chunks[chunk.Id] = (chunk, terms.Count);
            foreach (var term in terms)
            {
                if (!_postings.TryGetValue(term, out var postings))
                {
                    _postings[term] = postings = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                postings[chunk.Id] = postings.GetValueOrDefault(chunk.Id) + 1;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var averageLength = _chunks.Count == 0 ? 1 : _chunks.Values.Average(c => c.Length);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var term in Terms(question).Distinct())
        {
            if (!_postings.TryGetValue(term, out var postings))
            {
                continue;
            }

            var idf = Math.Log(1 + (_chunks.Count - postings.Count + 0.5) / (postings.Count + 0.5));
            foreach (var (id, tf) in postings)
            {
                var (chunk, length) = _chunks[id];
                if (!asker.CanRead(chunk.Document))
                {
                    continue; // permissions first, then ranking
                }

                scores[id] = scores.GetValueOrDefault(id) + idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * length / averageLength));
            }
        }

        IReadOnlyList<RetrievedChunk> results =
            [.. scores.OrderByDescending(s => s.Value).Take(top).Select(s => new RetrievedChunk(_chunks[s.Key].Chunk, s.Value))];
        return Task.FromResult(results);
    }

    // Lowercase words with IDs kept whole, stop words dropped, plurals and -ing stemmed (Chapter 2's analyzer).
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
