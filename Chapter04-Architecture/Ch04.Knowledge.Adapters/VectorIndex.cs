using System.Linq.Expressions;
using Ch04.Knowledge.Core;
using Ch04.Knowledge.Core.Ports;
using Contoso.AI;
using Contoso.Corpus;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Ch04.Knowledge.Adapters;

/// <summary>A chunk as the vector store keeps it (Chapter 3's record).</summary>
public sealed class ChunkRecord
{
    public const int Dimensions = HashingEmbeddingGenerator.DefaultDimensions;

    [VectorStoreKey]
    public string Id { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string DocumentId { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string[] AllowedGroups { get; set; } = [];

    [VectorStoreData]
    public string Text { get; set; } = "";

    [VectorStoreVector(Dimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>
/// Dense retrieval on Microsoft.Extensions.VectorData. One adapter serves both planes: the ingestion
/// plane writes through <see cref="IIndexWriter"/> and the query plane reads through <see cref="IRetriever"/>.
/// </summary>
public sealed class VectorIndex(VectorStore store, IEmbeddingGenerator<string, Embedding<float>> embedder)
    : IIndexWriter, IRetriever
{
    private readonly VectorStoreCollection<string, ChunkRecord> _records = store.GetCollection<string, ChunkRecord>("chunks");

    // The in-memory adapter keeps the chunks it wrote so it can return them whole; a durable store
    // would rebuild them from stored fields (Chapter 10).
    private readonly Dictionary<string, Chunk> _chunks = new(StringComparer.Ordinal);

    public async Task WriteAsync(IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken = default)
    {
        await _records.EnsureCollectionExistsAsync(cancellationToken);
        foreach (var batch in chunks.Chunk(32))
        {
            var embeddings = await embedder.GenerateAsync(batch.Select(c => c.Text), cancellationToken: cancellationToken);
            await _records.UpsertAsync(batch.Zip(embeddings, (chunk, embedding) => new ChunkRecord
            {
                Id = chunk.Id,
                DocumentId = chunk.Document.Id,
                AllowedGroups = [.. chunk.Document.AllowedGroups],
                Text = chunk.Text,
                Embedding = embedding.Vector,
            }), cancellationToken);

            foreach (var chunk in batch)
            {
                _chunks[chunk.Id] = chunk;
            }
        }
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var query = await embedder.GenerateVectorAsync(question, cancellationToken: cancellationToken);
        var options = new VectorSearchOptions<ChunkRecord> { Filter = ReadableBy(asker) };

        var results = new List<RetrievedChunk>(top);
        await foreach (var hit in _records.SearchAsync(query, top, options, cancellationToken))
        {
            results.Add(new RetrievedChunk(_chunks[hit.Record.Id], hit.Score ?? 0));
        }

        return results;
    }

    // r => r.AllowedGroups.Contains(g1) || r.AllowedGroups.Contains(g2) || ... (Chapter 3's filter)
    private static Expression<Func<ChunkRecord, bool>> ReadableBy(Person asker)
    {
        var record = Expression.Parameter(typeof(ChunkRecord), "r");
        var allowed = Expression.Property(record, nameof(ChunkRecord.AllowedGroups));
        var body = asker.Groups.Order(StringComparer.Ordinal)
            .Select(group => (Expression)Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)], allowed, Expression.Constant(group)))
            .Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<ChunkRecord, bool>>(body, record);
    }
}
