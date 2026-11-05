using CommunityToolkit.VectorData.Qdrant;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Testcontainers.Qdrant;

namespace Ch10.Embeddings.Cli;

/// <summary>An index version: the collection and the model that wrote its vectors, always switched together.</summary>
public sealed record IndexVersion(string Collection, string Model, IEmbeddingGenerator<string, Embedding<float>> Passages, IEmbeddingGenerator<string, Embedding<float>> Queries);

/// <summary>
/// Re-embedding without downtime: the old index keeps serving while a shadow index is built with the new model and
/// every change is written to both; then the query path switches to the new (collection, model) pair in one step and
/// the alias other readers use moves with it.
/// </summary>
public static class Lifecycle
{
    public static async Task RunAsync(Corpus corpus, IReadOnlyList<Chunk> chunks, IReadOnlyList<GoldenQuestion> questions)
    {
        await using var container = new QdrantBuilder(Indexes.QdrantImage).Build();
        await container.StartAsync();
        var client = new QdrantClient(new Uri(container.GetGrpcConnectionString()));
        var store = new QdrantVectorStore(client, ownsClient: false);
        using var model = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true));
        var hashing = new HashingEmbeddingGenerator();
        var v1 = new IndexVersion("chunks-hashing-v1", "hashing-384", hashing, hashing);
        var v2 = new IndexVersion("chunks-bge-small-int8-v2", model.Spec.Name, new OnnxEmbeddingGenerator(model), new OnnxEmbeddingGenerator(model, queries: true));

        await BuildAsync(v1, chunks);
        await client.CreateAliasAsync("chunks", v1.Collection);
        var active = v1;

        // Readers run the dev questions the whole time; each takes a snapshot of the active version for one query.
        using var stop = new CancellationTokenSource();
        var served = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var (failures, mismatches) = (0, 0);
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(async () =>
        {
            for (var i = r; !stop.IsCancellationRequested; i++)
            {
                var version = Volatile.Read(ref active);
                var question = questions[i % questions.Count];
                try
                {
                    var vector = (await version.Queries.GenerateAsync([question.Question]))[0].Vector;
                    var options = new VectorSearchOptions<ChunkRecord> { Filter = AccessFilter.ReadableBy(People.Find(question.Asker)!) };
                    await foreach (var hit in store.GetCollection<Guid, ChunkRecord>(version.Collection).SearchAsync(vector, 4, options))
                    {
                        if (hit.Record.EmbeddingModel != version.Model) Interlocked.Increment(ref mismatches);
                    }

                    served.AddOrUpdate(version.Model, 1, (_, n) => n + 1);
                }
                catch (Exception) when (!stop.IsCancellationRequested)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        })).ToList();

        // Build the shadow index with the new model while v1 serves; a change during the backfill goes to both.
        var started = TimeProvider.System.GetTimestamp();
        await BuildAsync(v2, chunks);
        var edited = chunks[0] with { Text = chunks[0].Text + " Revised 2027-01-28." };
        foreach (var version in new[] { v1, v2 }) await WriteAsync(version, [edited]);
        var built = TimeProvider.System.GetElapsedTime(started);

        // Cut over: the application's (collection, model) pair in one assignment, the alias in one request.
        Volatile.Write(ref active, v2);
        await client.UpdateAliasesAsync(
        [
            new AliasOperations { DeleteAlias = new DeleteAlias { AliasName = "chunks" } },
            new AliasOperations { CreateAlias = new CreateAlias { AliasName = "chunks", CollectionName = v2.Collection } },
        ]);
        await Task.Delay(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        var aliases = await client.ListCollectionAliasesAsync(v2.Collection);
        Console.WriteLine($"Shadow index {v2.Collection}: {chunks.Count} chunks embedded and written in {built.TotalSeconds:F1} s while {v1.Collection} served");
        Console.WriteLine($"Queries served: {string.Join(", ", served.OrderBy(s => s.Key).Select(s => $"{s.Key} {s.Value}"))}; failed {failures}; vectors from the wrong model {mismatches}");
        Console.WriteLine($"Alias \"chunks\" now points to {v2.Collection} ({aliases.Count} alias)");

        async Task BuildAsync(IndexVersion version, IReadOnlyList<Chunk> all)
        {
            await store.GetCollection<Guid, ChunkRecord>(version.Collection).EnsureCollectionExistsAsync();
            await WriteAsync(version, all);
        }

        async Task WriteAsync(IndexVersion version, IReadOnlyList<Chunk> batch)
        {
            var embeddings = await version.Passages.GenerateAsync(batch.Select(c => c.IndexText));
            await store.GetCollection<Guid, ChunkRecord>(version.Collection).UpsertAsync(batch.Select((c, i) => new ChunkRecord
            {
                Id = ChunkRecord.KeyFor(c.Doc, c.Start, c.End, c.IndexText),
                DocumentId = c.Doc,
                Tenant = corpus.Documents[c.Doc].Tenant,
                AllowedGroups = [.. corpus.Documents[c.Doc].AllowedGroups],
                Text = c.IndexText,
                ReturnStart = c.ReturnStart,
                ReturnEnd = c.ReturnEnd,
                EmbeddingModel = version.Model,
                Embedding = embeddings[i].Vector,
            }));
        }
    }
}
