using Contoso.Corpus;

namespace Ch02.Fundamentals;

/// <summary>A ranked document, its BM25 score and the query terms it matched.</summary>
public sealed record ScoredDocument(SourceDocument Document, double Score, IReadOnlyList<string> MatchedTerms);

/// <summary>
/// An in-memory inverted index ranked with BM25 (k1 = 1.2 and b = 0.75 are Lucene's defaults).
/// Statistics cover the whole index; results are filtered to what the asker may read.
/// Chapter 11 replaces it with Lucene.NET.
/// </summary>
public sealed class InvertedIndex(bool stem = false, double k1 = 1.2, double b = 0.75)
{
    // term -> (document ID -> how often the term occurs in that document)
    private readonly Dictionary<string, Dictionary<string, int>> _postings = new();
    private readonly Dictionary<string, (SourceDocument Document, int Length)> _documents = new();

    public static InvertedIndex Create(IEnumerable<SourceDocument> documents, bool stem = false)
    {
        var index = new InvertedIndex(stem);
        foreach (var document in documents)
        {
            index.Add(document);
        }

        return index;
    }

    public void Add(SourceDocument document)
    {
        var terms = Analyzer.Terms($"{document.Title} {document.Text}", stem);
        _documents[document.Id] = (document, terms.Count);
        foreach (var term in terms)
        {
            if (!_postings.TryGetValue(term, out var postings))
            {
                _postings[term] = postings = new Dictionary<string, int>();
            }

            postings[document.Id] = postings.GetValueOrDefault(document.Id) + 1;
        }
    }

    /// <summary>Inverse document frequency: rare terms count for more than common ones.</summary>
    public double Idf(string term)
    {
        var n = _postings.TryGetValue(term, out var postings) ? postings.Count : 0;
        return Math.Log(1 + (_documents.Count - n + 0.5) / (n + 0.5));
    }

    public IReadOnlyList<ScoredDocument> Search(string query, Person asker, int top = 3)
    {
        var averageLength = _documents.Values.Average(d => d.Length);
        var scores = new Dictionary<string, (double Score, List<string> Terms)>();

        foreach (var term in Analyzer.Terms(query, stem).Distinct())
        {
            if (!_postings.TryGetValue(term, out var postings))
            {
                continue;
            }

            var idf = Idf(term);
            foreach (var (id, tf) in postings)
            {
                var length = _documents[id].Length;
                var termScore = idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * length / averageLength));
                var (score, matched) = scores.TryGetValue(id, out var entry) ? entry : (0, []);
                matched.Add(term);
                scores[id] = (score + termScore, matched);
            }
        }

        return scores
            .Where(s => asker.CanRead(_documents[s.Key].Document))
            .OrderByDescending(s => s.Value.Score)
            .Take(top)
            .Select(s => new ScoredDocument(_documents[s.Key].Document, s.Value.Score, s.Value.Terms))
            .ToList();
    }
}
