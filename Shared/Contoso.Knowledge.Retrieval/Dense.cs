using System.Numerics.Tensors;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Retrieval;

/// <summary>
/// Dense retrieval by exact search over normalized vectors held in memory: the baseline any vector index is measured
/// against, and fast enough for a few hundred thousand chunks. Chapter 10's pgvector index serves the same role in
/// production.
/// </summary>
public sealed class DenseRetriever : IRetriever
{
    private readonly IReadOnlyList<IndexedChunk> _chunks;
    private readonly float[][] _vectors;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _queries;

    private DenseRetriever(string name, IReadOnlyList<IndexedChunk> chunks, float[][] vectors, IEmbeddingGenerator<string, Embedding<float>> queries) =>
        (Name, _chunks, _vectors, _queries) = (name, chunks, vectors, queries);

    public string Name { get; }

    public static async Task<DenseRetriever> CreateAsync(string name, IReadOnlyList<IndexedChunk> chunks,
        IEmbeddingGenerator<string, Embedding<float>> passages, IEmbeddingGenerator<string, Embedding<float>> queries,
        CancellationToken cancellationToken = default)
    {
        var embeddings = await passages.GenerateAsync(chunks.Select(c => c.Chunk.IndexText), cancellationToken: cancellationToken);
        return new DenseRetriever(name, chunks, [.. embeddings.Select(e => e.Vector.ToArray())], queries);
    }

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var query = (await _queries.GenerateAsync([request.Question], cancellationToken: cancellationToken))[0].Vector.ToArray();
        var ranked = Enumerable.Range(0, _chunks.Count)
            .Where(i => _chunks[i].Passes(request.Asker, request.Filter))
            .Select(i => new Hit(_chunks[i], TensorPrimitives.Dot<float>(query, _vectors[i]), Name))
            .OrderByDescending(h => h.Score);
        return RetrievalResult.Of(Ranking.DistinctReturns(ranked, request.Top));
    }
}
