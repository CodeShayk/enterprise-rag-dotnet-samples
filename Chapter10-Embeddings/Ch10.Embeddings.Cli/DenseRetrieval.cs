using System.Numerics.Tensors;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.AI;

namespace Ch10.Embeddings.Cli;

/// <summary>
/// Dense retrieval as a configuration for Chapter 5's harness: each chunk's index text embedded as a passage, the
/// question embedded as a query, cosine similarity over normalized vectors, the asker's permissions applied inside the
/// search, and the top distinct return spans, so a parent counts once.
/// </summary>
public sealed class DenseRetrieval : IRetrievalUnderTest
{
    private readonly IReadOnlyList<Chunk> _chunks;
    private readonly float[][] _vectors;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _queries;
    private readonly IReadOnlyDictionary<string, SourceDocument> _documents;

    private DenseRetrieval(string name, IReadOnlyList<Chunk> chunks, float[][] vectors, IEmbeddingGenerator<string, Embedding<float>> queries,
        IReadOnlyDictionary<string, SourceDocument> documents)
    {
        (Name, _chunks, _vectors, _queries, _documents) = (name, chunks, vectors, queries, documents);
        Chunks = [.. chunks.Select(c => new ChunkSpan(c.Doc, c.ReturnStart, c.ReturnEnd)).Distinct()];
        Parameters = new Dictionary<string, string> { ["chunks"] = chunks.Count.ToString(), ["retriever"] = "cosine, exact" };
    }

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    public IReadOnlyList<ChunkSpan> Chunks { get; }

    public static async Task<DenseRetrieval> CreateAsync(string name, IReadOnlyList<Chunk> chunks, IEmbeddingGenerator<string, Embedding<float>> passages,
        IEmbeddingGenerator<string, Embedding<float>> queries, IReadOnlyDictionary<string, SourceDocument> documents)
    {
        var embeddings = await passages.GenerateAsync(chunks.Select(c => c.IndexText));
        return new DenseRetrieval(name, chunks, [.. embeddings.Select(e => Normalized(e.Vector.Span))], queries, documents);
    }

    public async Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var query = Normalized((await _queries.GenerateAsync([question], cancellationToken: cancellationToken))[0].Vector.Span);
        var ranked = Enumerable.Range(0, _chunks.Count)
            .Where(i => asker.CanRead(_documents[_chunks[i].Doc]))
            .OrderByDescending(i => TensorPrimitives.Dot(query, _vectors[i]));
        var results = new List<ChunkSpan>(top);
        foreach (var i in ranked)
        {
            var span = new ChunkSpan(_chunks[i].Doc, _chunks[i].ReturnStart, _chunks[i].ReturnEnd);
            if (!results.Contains(span)) results.Add(span);
            if (results.Count == top) break;
        }

        return results;
    }

    private static float[] Normalized(ReadOnlySpan<float> vector)
    {
        var copy = vector.ToArray();
        TensorPrimitives.Divide(copy, TensorPrimitives.Norm(copy), copy);
        return copy;
    }
}
