using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.VectorData.PgVector;
using CommunityToolkit.VectorData.Qdrant;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Embeddings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Npgsql;
using Qdrant.Client;
using Testcontainers.PostgreSql;
using Testcontainers.Qdrant;

namespace Ch10.Embeddings.Cli;

/// <summary>
/// One child chunk as both stores keep it. Qdrant accepts only Guid or integer keys, so the key is a Guid made from
/// the chunk's content hash, which keeps upserts idempotent. The model name travels with every vector (10.10).
/// </summary>
public sealed class ChunkRecord
{
    [VectorStoreKey]
    public Guid Id { get; set; }

    [VectorStoreData(IsIndexed = true)]
    public string DocumentId { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string Tenant { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string[] AllowedGroups { get; set; } = [];

    [VectorStoreData]
    public string Text { get; set; } = "";

    [VectorStoreData]
    public int ReturnStart { get; set; }

    [VectorStoreData]
    public int ReturnEnd { get; set; }

    [VectorStoreData(IsIndexed = true)]
    public string EmbeddingModel { get; set; } = "";

    [VectorStoreVector(384, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }

    public static Guid KeyFor(string doc, int start, int end, string text) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{doc}\n{start}\n{end}\n{text}")).AsSpan(0, 16));
}

/// <summary>Chapter 3's permission filter for this record: equality, || and Contains, the shapes every connector translates.</summary>
public static class AccessFilter
{
    public static Expression<Func<ChunkRecord, bool>> ReadableBy(Person asker)
    {
        var record = Expression.Parameter(typeof(ChunkRecord), "r");
        var allowed = Expression.Property(record, nameof(ChunkRecord.AllowedGroups));
        var body = asker.Groups.Order(StringComparer.Ordinal)
            .Select(group => (Expression)Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)], allowed, Expression.Constant(group)))
            .Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<ChunkRecord, bool>>(body, record);
    }
}

public static class Stores
{
    /// <summary>The same records, the same filter and the same query in pgvector and Qdrant through Microsoft.Extensions.VectorData.</summary>
    public static async Task RunAsync(Corpus corpus, IReadOnlyList<Chunk> chunks)
    {
        await using var postgres = new PostgreSqlBuilder(Indexes.PgvectorImage).Build();
        await using var qdrantContainer = new QdrantBuilder(Indexes.QdrantImage).Build();
        await Task.WhenAll(postgres.StartAsync(), qdrantContainer.StartAsync());

        using var model = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true));
        var passages = new OnnxEmbeddingGenerator(model);
        var queries = new OnnxEmbeddingGenerator(model, queries: true);
        var embeddings = await passages.GenerateAsync(chunks.Select(c => c.IndexText));
        var records = chunks.Select((c, i) => new ChunkRecord
        {
            Id = ChunkRecord.KeyFor(c.Doc, c.Start, c.End, c.IndexText),
            DocumentId = c.Doc,
            Tenant = corpus.Documents[c.Doc].Tenant,
            AllowedGroups = [.. corpus.Documents[c.Doc].AllowedGroups],
            Text = c.IndexText,
            ReturnStart = c.ReturnStart,
            ReturnEnd = c.ReturnEnd,
            EmbeddingModel = model.Spec.Name,
            Embedding = embeddings[i].Vector,
        }).ToList();

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgres.GetConnectionString());
        dataSourceBuilder.UseVector();
        VectorStore[] stores =
        [
            new PostgresVectorStore(dataSourceBuilder.Build(), ownsDataSource: true),
            new QdrantVectorStore(new QdrantClient(new Uri(qdrantContainer.GetGrpcConnectionString())), ownsClient: true),
        ];

        var results = new List<List<string>>();
        foreach (var store in stores)
        {
            var collection = store.GetCollection<Guid, ChunkRecord>("chunks");
            await collection.EnsureCollectionExistsAsync();
            var started = TimeProvider.System.GetTimestamp();
            await collection.UpsertAsync(records);
            Console.WriteLine($"{store.GetType().Name}: {records.Count} records upserted in {TimeProvider.System.GetElapsedTime(started).TotalMilliseconds:F0} ms");

            var lines = new List<string>();
            foreach (var (person, question) in new[]
            {
                ("tomas.silva", "What torque applies to the M12 flange bolts on P-201?"),
                ("grace.liu", "What warranty does Fabrikam Seals give on mechanical seals?"),
                ("tomas.silva", "What warranty does Fabrikam Seals give on mechanical seals?"),
            })
            {
                var asker = People.Find(person)!;
                var vector = (await queries.GenerateAsync([question]))[0].Vector;
                var options = new VectorSearchOptions<ChunkRecord> { Filter = AccessFilter.ReadableBy(asker) };
                var hits = new List<string>();
                await foreach (var hit in collection.SearchAsync(vector, 3, options)) hits.Add($"{hit.Record.DocumentId} {hit.Score:F3}");
                lines.Add($"  {asker.Name,-14} {question[..Math.Min(question.Length, 44)],-44} {string.Join("; ", hits)}");
            }

            foreach (var line in lines) Console.WriteLine(line);
            results.Add(lines);
        }

        Console.WriteLine(results[0].SequenceEqual(results[1]) ? "Both stores returned the same chunks with the same scores." : "The stores disagree.");
    }
}
