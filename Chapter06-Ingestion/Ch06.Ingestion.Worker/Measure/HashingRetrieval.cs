using System.Numerics.Tensors;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;

namespace Ch06.Ingestion.Worker.Measure;

/// <summary>
/// Chapter 6's search as a configuration for Chapter 5's harness: hashing embeddings of each chunk's section path and
/// text, cosine similarity, the asker's permissions inside the query and the top distinct spans.
/// </summary>
public sealed class HashingRetrieval(string name, IReadOnlyList<(ChunkSpan Span, float[] Vector)> chunks, IReadOnlyDictionary<string, SourceDocument> documents)
    : IRetrievalUnderTest
{
    private readonly IReadOnlyList<(ChunkSpan Span, float[] Vector)> _chunks = chunks;
    private readonly HashingEmbeddingGenerator _embedder = new();

    public string Name => name;

    public IReadOnlyDictionary<string, string> Parameters { get; } = new Dictionary<string, string> { ["search"] = "hashing embeddings, cosine" };

    public IReadOnlyList<ChunkSpan> Chunks => [.. _chunks.Select(c => c.Span).Distinct()];

    public Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var query = _embedder.Embed(question);
        IReadOnlyList<ChunkSpan> ranked = [.. _chunks.Where(c => asker.CanRead(documents[c.Span.Doc]))
            .OrderByDescending(c => TensorPrimitives.CosineSimilarity(query, c.Vector))
            .Select(c => c.Span).Distinct().Take(top)];
        return Task.FromResult(ranked);
    }
}
