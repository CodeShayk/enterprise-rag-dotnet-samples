using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Microsoft.ML.Tokenizers;

namespace Ch05.Evaluation.Cli;

/// <summary>A chunk with its place in the document, so the harness can match it against labeled spans.</summary>
public sealed record IndexedChunk(SourceDocument Document, ChunkSpan Span, string Text);

/// <summary>The configuration matrix: two retrievers (Chapter 3's and Chapter 4's) at two chunk sizes.</summary>
public static class Configurations
{
    public static IReadOnlyList<IRetrievalUnderTest> Matrix(IReadOnlyList<SourceDocument> corpus, Tokenizer tokenizer) =>
    [
        new VectorRetrieval(Chunk(corpus, tokenizer, 100, 20), "vector-100", 100),
        new VectorRetrieval(Chunk(corpus, tokenizer, 200, 40), "vector-200", 200),
        new KeywordRetrieval(Chunk(corpus, tokenizer, 100, 20), "keyword-100", 100),
        new KeywordRetrieval(Chunk(corpus, tokenizer, 200, 40), "keyword-200", 200),
    ];

    /// <summary>Chapter 3's fixed-size chunker, keeping each window's character range.</summary>
    public static IReadOnlyList<IndexedChunk> Chunk(IReadOnlyList<SourceDocument> corpus, Tokenizer tokenizer, int chunkTokens, int overlapTokens)
    {
        var chunks = new List<IndexedChunk>();
        foreach (var document in corpus)
        {
            var text = document.Text;
            var start = 0;
            while (start < text.Length)
            {
                var length = tokenizer.GetIndexByTokenCount(text[start..], chunkTokens, out _, out _);
                chunks.Add(new IndexedChunk(document, new ChunkSpan(document.Id, start, start + length), text.Substring(start, length)));
                if (start + length >= text.Length)
                {
                    break;
                }

                start += Math.Max(tokenizer.GetIndexByTokenCountFromEnd(text.Substring(start, length), overlapTokens, out _, out _), 1);
            }
        }

        return chunks;
    }
}

/// <summary>Chapter 3's retrieval: hashing embeddings, cosine similarity, permissions before ranking.</summary>
public sealed class VectorRetrieval : IRetrievalUnderTest
{
    private readonly IReadOnlyList<IndexedChunk> _chunks;
    private readonly HashingEmbeddingGenerator _embedder = new();
    private readonly float[][] _vectors;

    public VectorRetrieval(IReadOnlyList<IndexedChunk> chunks, string name, int chunkTokens)
    {
        _chunks = chunks;
        _vectors = [.. chunks.Select(c => _embedder.Embed(c.Text))];
        Name = name;
        Parameters = new Dictionary<string, string> { ["retriever"] = "hashing vectors, cosine", ["chunkTokens"] = $"{chunkTokens}" };
    }

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    public IReadOnlyList<ChunkSpan> Chunks => [.. _chunks.Select(c => c.Span)];

    public Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var query = _embedder.Embed(question);
        IReadOnlyList<ChunkSpan> ranked = [.. Enumerable.Range(0, _chunks.Count)
            .Where(i => asker.CanRead(_chunks[i].Document))
            .OrderByDescending(i => TensorPrimitives.Dot(query, _vectors[i]))
            .Take(top)
            .Select(i => _chunks[i].Span)];
        return Task.FromResult(ranked);
    }
}

/// <summary>Chapter 4's keyword retrieval: BM25 over chunks, permissions before scoring.</summary>
public sealed partial class KeywordRetrieval : IRetrievalUnderTest
{
    private const double K1 = 1.2, B = 0.75;
    private readonly IReadOnlyList<IndexedChunk> _chunks;
    private readonly List<Dictionary<string, int>> _termCounts;
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    public KeywordRetrieval(IReadOnlyList<IndexedChunk> chunks, string name, int chunkTokens)
    {
        _chunks = chunks;
        _termCounts = [.. chunks.Select(c => Terms(c.Text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal))];
        foreach (var term in _termCounts.SelectMany(counts => counts.Keys))
        {
            _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
        }

        _averageLength = _termCounts.Average(counts => counts.Values.Sum());
        Name = name;
        Parameters = new Dictionary<string, string> { ["retriever"] = "BM25 (k1 1.2, b 0.75), stemmed", ["chunkTokens"] = $"{chunkTokens}" };
    }

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    public IReadOnlyList<ChunkSpan> Chunks => [.. _chunks.Select(c => c.Span)];

    public Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var terms = Terms(question).Distinct().ToList();
        IReadOnlyList<ChunkSpan> ranked = [.. Enumerable.Range(0, _chunks.Count)
            .Where(i => asker.CanRead(_chunks[i].Document))
            .Select(i => (Index: i, Score: Score(terms, i)))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Take(top)
            .Select(r => _chunks[r.Index].Span)];
        return Task.FromResult(ranked);
    }

    private double Score(List<string> terms, int i)
    {
        var length = _termCounts[i].Values.Sum();
        double score = 0;
        foreach (var term in terms)
        {
            if (_termCounts[i].TryGetValue(term, out var tf))
            {
                var n = _documentFrequency[term];
                var idf = Math.Log(1 + (_chunks.Count - n + 0.5) / (n + 0.5));
                score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * length / _averageLength));
            }
        }

        return score;
    }

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
