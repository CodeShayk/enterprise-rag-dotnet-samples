using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Operations;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ch24.Deployment.Cli;

/// <summary>How an index version is built: the chunk sizes, whether breadcrumbs go into the indexed text, and the embedding model.</summary>
public sealed record IndexConfig(string Name, int Child, int Parent, bool Breadcrumbs, bool Int8)
{
    public string EmbeddingModel => Int8 ? "bge-small-en-v1.5-int8" : "bge-small-en-v1.5-fp32";
}

/// <summary>One chunk of a stored index version, with its vector.</summary>
public sealed record StoredChunk(
    int Ordinal, string DocumentId, string Collection, string DocumentType, IReadOnlyList<string> EquipmentTags, DateOnly? EffectiveFrom, DateOnly? EffectiveTo,
    int Start, int End, string Text, string? Header, int? ReturnStart, int? ReturnEnd, string Kind, float[] Vector);

/// <summary>
/// What every command shares, copied from Chapter 23 because chapters don't reference each other: the corpus, golden
/// set v2 and the shipped retriever, plus index versions built from a configuration, stored in a catalog and loaded
/// back as the retriever a service would run.
/// </summary>
public sealed class Lab
{
    public static readonly IndexConfig Shipped = new("shipped", 100, 600, true, true);

    public static readonly IReadOnlyList<IndexConfig> Candidates =
    [
        Shipped,
        new("child-200", 200, 600, true, true),
        new("parent-400", 100, 400, true, true),
        new("no-breadcrumbs", 100, 600, false, true),
        new("bge-fp32", 100, 600, true, false),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyList<(RegisterEntry Entry, ChunkSource Source, string DocumentType, IReadOnlyList<string> Tags, DateOnly? From, DateOnly? To)> Sources { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public required string CorpusSha { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static string Runs { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch24"));

    public static DateTimeOffset Today { get; } = new(2027, 2, 1, 7, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<GoldenQuestion> Split(string split) => [.. Questions.Where(q => q.Split == split)];

    public static async Task<Lab> LoadAsync()
    {
        var (documents, sources) = (new Dictionary<string, SourceDocument>(StringComparer.Ordinal), new List<(RegisterEntry, ChunkSource, string, IReadOnlyList<string>, DateOnly?, DateOnly?)>());
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            documents[entry.Id] = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            sources.Add((entry, source, m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
        }

        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        var corpusSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', sources.Select(s => s.Item1.Sha256)))))[..12];
        return new Lab { Documents = documents, Sources = sources, Questions = GoldenSet.Load(path), CorpusSha = corpusSha };
    }

    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" or "upload" => "notices and reports",
        _ => "policies and bulletins",
    };

    /// <summary>The corpus chunked by a configuration, breadcrumbs dropped from the indexed text when it says so.</summary>
    public async Task<IReadOnlyList<IndexedChunk>> ChunkAsync(IndexConfig config)
    {
        var chunker = new ParentChildChunker(O200k, config.Child, config.Parent);
        var chunks = new List<IndexedChunk>();
        foreach (var (entry, source, type, tags, from, to) in Sources)
        {
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                chunks.Add(new IndexedChunk(chunks.Count, config.Breadcrumbs ? chunk : chunk with { Header = null }, Documents[entry.Id], Collection(type), type, tags, from, to));
            }
        }

        return chunks;
    }

    private readonly Dictionary<bool, OnnxEmbeddingModel> _models = [];
    private OnnxCrossEncoder? _crossEncoder;

    public OnnxEmbeddingModel Model(bool int8) => _models.TryGetValue(int8, out var m) ? m : _models[int8] = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8), 0, spinning: false);

    public OnnxCrossEncoder CrossEncoder => _crossEncoder ??= new("MiniLM-L6",
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"), Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);

    /// <summary>Builds an index version: chunks the corpus, embeds every chunk and writes chunks, vectors and the manifest to the catalog.</summary>
    public async Task<IndexManifest> BuildAsync(IndexCatalog catalog, string version, IndexConfig config)
    {
        var chunks = await ChunkAsync(config);
        var vectors = await new OnnxEmbeddingGenerator(Model(config.Int8)).GenerateAsync(chunks.Select(c => c.Chunk.IndexText));
        await File.WriteAllLinesAsync(Path.Combine(catalog.PathOf(version), "chunks.jsonl"), chunks.Select((c, i) => JsonSerializer.Serialize(new StoredChunk(
            c.Ordinal, c.Document.Id, c.Collection, c.DocumentType, c.EquipmentTags, c.EffectiveFrom, c.EffectiveTo,
            c.Chunk.Start, c.Chunk.End, c.Chunk.Text, c.Chunk.Header, c.Chunk.Returns?.Start, c.Chunk.Returns?.End, c.Chunk.Kind, vectors[i].Vector.ToArray()), Json)));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(EmbeddingModels.BgeSmall(config.Int8).ModelPath)))[..12];
        var manifest = new IndexManifest(version, $"parent-child-{config.Child}-{config.Parent}", config.Breadcrumbs, config.EmbeddingModel, sha, chunks.Count, CorpusSha, Today);
        catalog.SaveManifest(manifest);
        return manifest;
    }

    /// <summary>The retriever a service runs over a stored version: Lucene and the stored vectors, fused with RRF, re-ranked by MiniLM-L6.</summary>
    public async Task<ServedIndex> LoadAsync(IndexCatalog catalog, string version, bool queryInt8 = true, Func<IRetriever, IRetriever>? wrapDense = null)
    {
        var stored = File.ReadLines(Path.Combine(catalog.PathOf(version), "chunks.jsonl")).Select(l => JsonSerializer.Deserialize<StoredChunk>(l, Json)!).ToList();
        var chunks = stored.Select(s => new IndexedChunk(s.Ordinal,
            new Chunk(s.DocumentId, s.Start, s.End, s.Text) { Header = s.Header, Kind = s.Kind, Returns = s.ReturnStart is { } rs ? (rs, s.ReturnEnd!.Value) : null },
            Documents[s.DocumentId], s.Collection, s.DocumentType, s.EquipmentTags, s.EffectiveFrom, s.EffectiveTo)).ToList();
        var byText = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var (chunk, s) in chunks.Zip(stored)) byText[chunk.Chunk.IndexText] = s.Vector; // identical text, identical vector
        var vectors = new StoredEmbeddings(byText);
        var keyword = new LuceneRetriever("keyword", chunks, ContosoAnalyzers.Identifiers());
        IRetriever dense = await DenseRetriever.CreateAsync("dense", chunks, vectors, new OnnxEmbeddingGenerator(Model(queryInt8), queries: true));
        var hybrid = new HybridRetriever("hybrid", [keyword, wrapDense?.Invoke(dense) ?? dense], new ReciprocalRankFusion(60));
        return new ServedIndex(new RerankingRetriever("hybrid, re-ranked", hybrid, new OnnxCrossEncoderReranker(CrossEncoder), candidates: 8), chunks);
    }

    /// <summary>Sufficiency per question: does the top 4 hold every labeled fact (Chapter 5's harness)?</summary>
    public async Task<IReadOnlyDictionary<string, double>> ScoreAsync(ServedIndex index, IReadOnlyList<GoldenQuestion> questions)
    {
        var harness = new Harness([.. Documents.Values], GoldenSet.Sha256(Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl")));
        var run = await harness.RunAsync(new Configuration(index.Retriever, index.Chunks), questions, 4, questions[0].Split);
        return run.Answerable.ToDictionary(q => q.Id, q => q.Sufficient);
    }
}

/// <summary>An index version as a service runs it.</summary>
public sealed record ServedIndex(IRetriever Retriever, IReadOnlyList<IndexedChunk> Chunks);

/// <summary>Vectors read from an index version: a lookup by text, so the stored index is used as built.</summary>
public sealed class StoredEmbeddings(IReadOnlyDictionary<string, float[]> vectors) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(vectors[v]))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>An <see cref="IRetriever"/> as a configuration for Chapter 5's harness.</summary>
public sealed class Configuration(IRetriever retriever, IReadOnlyList<IndexedChunk> chunks) : IRetrievalUnderTest
{
    public string Name => retriever.Name;

    public IReadOnlyDictionary<string, string> Parameters { get; } = new Dictionary<string, string> { ["retriever"] = retriever.Name };

    public IReadOnlyList<ChunkSpan> Chunks { get; } = [.. chunks.Select(c => new ChunkSpan(c.Chunk.Doc, c.Chunk.ReturnStart, c.Chunk.ReturnEnd)).Distinct()];

    public async Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var result = await retriever.RetrieveAsync(new RetrievalRequest(question, asker, top), cancellationToken);
        return [.. result.Hits.Select(h => new ChunkSpan(h.Chunk.Chunk.Doc, h.Chunk.Chunk.ReturnStart, h.Chunk.Chunk.ReturnEnd))];
    }
}
