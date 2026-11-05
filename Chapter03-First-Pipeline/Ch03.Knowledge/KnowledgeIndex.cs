using Contoso.Corpus;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Ch03.Knowledge;

/// <summary>A chunk the search returned for one question, with its similarity score.</summary>
public sealed record RetrievedChunk(string ChunkId, string DocumentId, string Title, string Text, double Score);

/// <summary>What one indexing run did.</summary>
public sealed record IndexingReport(int Documents, int Chunks, int Tokens, TimeSpan Elapsed);

/// <summary>Chunks, embeds and stores documents; embeds questions and searches the stored chunks.</summary>
public sealed class KnowledgeIndex(
    VectorStore store,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    FixedSizeChunker chunker)
{
    public const string CollectionName = "chunks";

    private const int EmbeddingBatchSize = 32;

    private readonly VectorStoreCollection<string, ChunkRecord> _chunks =
        store.GetCollection<string, ChunkRecord>(CollectionName);

    public async Task<IndexingReport> IndexAsync(IReadOnlyList<SourceDocument> documents, CancellationToken cancellationToken = default)
    {
        var started = TimeProvider.System.GetTimestamp();
        await _chunks.EnsureCollectionExistsAsync(cancellationToken);

        var chunks = documents.SelectMany(chunker.Split).ToList();
        foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
        {
            var embeddings = await embedder.GenerateAsync(batch.Select(c => c.Text), cancellationToken: cancellationToken);
            if (embeddings[0].Vector.Length != ChunkRecord.Dimensions)
            {
                throw new InvalidOperationException(
                    $"The embedding model returned {embeddings[0].Vector.Length} dimensions, but the '{CollectionName}' " +
                    $"collection stores {ChunkRecord.Dimensions}. Configure a model with {ChunkRecord.Dimensions} dimensions.");
            }

            await _chunks.UpsertAsync(batch.Zip(embeddings, (chunk, embedding) => new ChunkRecord
            {
                Id = chunk.Id,
                DocumentId = chunk.Document.Id,
                Title = chunk.Document.Title,
                Tenant = chunk.Document.Tenant,
                AllowedGroups = [.. chunk.Document.AllowedGroups],
                Text = chunk.Text,
                Embedding = embedding.Vector,
            }), cancellationToken);
        }

        return new IndexingReport(documents.Count, chunks.Count, chunks.Sum(c => c.TokenCount),
            TimeProvider.System.GetElapsedTime(started));
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<float> query = await embedder.GenerateVectorAsync(question, cancellationToken: cancellationToken);

        // The permission filter runs inside the search, so top-k is the top k the asker may read.
        var options = new VectorSearchOptions<ChunkRecord> { Filter = AccessFilter.ReadableBy(asker) };

        var results = new List<RetrievedChunk>(top);
        await foreach (var hit in _chunks.SearchAsync(query, top, options, cancellationToken))
        {
            results.Add(new RetrievedChunk(hit.Record.Id, hit.Record.DocumentId, hit.Record.Title, hit.Record.Text, hit.Score ?? 0));
        }

        return results;
    }
}
