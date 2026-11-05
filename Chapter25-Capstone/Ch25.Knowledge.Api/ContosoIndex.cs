using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Graph;
using Contoso.Knowledge.Operations;
using Contoso.Knowledge.Retrieval;
using Contoso.Knowledge.Security;
using Lucene.Net.Analysis.Standard;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ch25.Knowledge.Api;

/// <summary>How an index version is built: which sources go in, the chunker, breadcrumbs and the embedding model.</summary>
public sealed record IndexSettings(string Name, bool Hygiene = true, bool ParentChild = true, bool Breadcrumbs = true, bool Int8 = true, bool WorkOrders = true)
{
    public static IndexSettings Shipped { get; } = new("shipped");

    public string EmbeddingModel => Int8 ? "bge-small-en-v1.5-int8" : "bge-small-en-v1.5-fp32";

    public string Chunker => ParentChild ? "parent-child-100-600" : "fixed-200-30";
}

/// <summary>How a served index searches: its sources, the keyword analyzer, re-ranking and where permissions apply.</summary>
public sealed record RetrievalSettings(bool Keyword = true, bool Dense = true, bool Identifiers = true, bool Rerank = true, bool TrimInSearch = true, bool Graph = true)
{
    public static RetrievalSettings Shipped { get; } = new();
}

/// <summary>One chunk of a stored index version, with its vector.</summary>
public sealed record StoredChunk(
    int Ordinal, string DocumentId, string Collection, string DocumentType, IReadOnlyList<string> EquipmentTags, DateOnly? EffectiveFrom, DateOnly? EffectiveTo,
    int Start, int End, string Text, string? Header, int? ReturnStart, int? ReturnEnd, string Kind, float[] Vector);

/// <summary>An index version as the service runs it.</summary>
public sealed record ServedIndex(string Version, IndexManifest Manifest, IRetriever Retriever, IReadOnlyList<IndexedChunk> Chunks);

/// <summary>
/// The corpus and its index versions, adapted from Chapter 24 because chapters don't reference each other: every
/// source in the generated corpus, the canonical ones marked, chunked and embedded into a version under the catalog
/// with its manifest, and loaded back as the retriever the service runs. The switches exist for the ablation.
/// </summary>
public sealed class ContosoIndex
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<bool, OnnxEmbeddingModel> _models = [];
    private readonly Lock _lock = new();
    private OnnxCrossEncoder? _crossEncoder;

    private ContosoIndex(IReadOnlyList<Source> sources)
    {
        _sources = sources;
        Documents = sources.Where(s => s.Canonical).ToDictionary(s => s.Document.Id, s => s.Document, StringComparer.Ordinal);
        Versions = sources.ToDictionary(s => s.Document.Id, s => s.Entry.Version, StringComparer.Ordinal);
        var groups = sources.SelectMany(s => s.Document.AllowedGroups).ToHashSet(StringComparer.Ordinal);
        Everyone = new Person("everyone", "Everyone", "", "corporate", groups);
    }

    private sealed record Source(RegisterEntry Entry, ChunkSource Text, SourceDocument Document, string? DocumentType, IReadOnlyList<string> Tags, DateOnly? From, DateOnly? To, bool Canonical);

    private readonly IReadOnlyList<Source> _sources;

    /// <summary>The documents the assistant answers from: no copies, no regional variants, no superseded revision.</summary>
    public IReadOnlyDictionary<string, SourceDocument> Documents { get; }

    /// <summary>Each document's version in its source system, for provenance.</summary>
    public IReadOnlyDictionary<string, string> Versions { get; }

    /// <summary>A document in a collection restricted to HR or Procurement, whose evidence stays inside Contoso's network (ADR-0001).</summary>
    public bool IsRestricted(string documentId) =>
        Documents.TryGetValue(documentId, out var d) && d.AllowedGroups.Any(g => g is "hr" or "people-managers" or "procurement");

    /// <summary>Someone who may read everything: what a search without permission trimming runs as.</summary>
    public Person Everyone { get; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static DateTimeOffset Today { get; } = new(2027, 2, 1, 7, 0, 0, TimeSpan.Zero);

    public static string GoldenSetPath { get; } = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");

    public static async Task<ContosoIndex> LoadAsync(CancellationToken cancellationToken = default)
    {
        var sources = new List<Source>();
        foreach (var entry in GeneratedCorpus.Register())
        {
            // Chapter 6's hygiene: copies, regional variants and SOP-FS-014's archived revision stay out of the index.
            var archived = entry.Id == "SOP-FS-014" && entry.Version != "3";
            var canonical = entry.CopyOf is null && !entry.Id.StartsWith("INTRANET-", StringComparison.Ordinal) && !archived;
            var id = archived ? $"{entry.Id}@{entry.Version}" : entry.Id;
            var truth = await GeneratedCorpus.TruthAsync(entry, cancellationToken);
            var m = truth.Metadata;
            var text = new ChunkSource(id, m.Title, truth);
            sources.Add(new Source(entry, text, new SourceDocument(id, m.Title, m.Tenant, m.AllowedGroups, text.Text), m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo, canonical));
        }

        return new ContosoIndex(sources);
    }

    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" or "upload" => "notices and reports",
        _ => "policies and bulletins",
    };

    /// <summary>Chapter 22's screen at ingestion: a document whose text reads like instructions to the model is quarantined for review, not indexed.</summary>
    public static IReadOnlyList<InjectionFinding> Screen(SourceDocument document) => InjectionDetector.Scan(document.Text);

    /// <summary>The documents a build with these settings would quarantine.</summary>
    public IReadOnlyList<string> Quarantined(IndexSettings settings) =>
        [.. Included(settings).Where(s => Screen(s.Document).Count > 0).Select(s => s.Document.Id)];

    private IEnumerable<Source> Included(IndexSettings settings) =>
        _sources.Where(s => (s.Canonical || !settings.Hygiene) && (settings.WorkOrders || s.DocumentType != "work-orders"));

    /// <summary>The corpus chunked as the settings say, quarantined documents left out.</summary>
    public async Task<IReadOnlyList<IndexedChunk>> ChunkAsync(IndexSettings settings)
    {
        IChunker chunker = settings.ParentChild ? new ParentChildChunker(O200k, 100, 600) : new FixedSizeChunker(O200k, 200, 30);
        var chunks = new List<IndexedChunk>();
        foreach (var s in Included(settings))
        {
            if (Screen(s.Document).Count > 0) continue;
            foreach (var chunk in await chunker.SplitAsync(s.Text))
            {
                chunks.Add(new IndexedChunk(chunks.Count, settings.Breadcrumbs ? chunk : chunk with { Header = null }, s.Document, Collection(s.DocumentType), s.DocumentType ?? "", s.Tags, s.From, s.To));
            }
        }

        return chunks;
    }

    public OnnxEmbeddingModel Model(bool int8)
    {
        lock (_lock) return _models.TryGetValue(int8, out var m) ? m : _models[int8] = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8), 0, spinning: false);
    }

    public OnnxCrossEncoder CrossEncoder
    {
        get
        {
            lock (_lock)
            {
                return _crossEncoder ??= new("MiniLM-L6", Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"),
                    Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);
            }
        }
    }

    /// <summary>Builds an index version: chunks the corpus, embeds every chunk and writes chunks, vectors and the manifest to the catalog.</summary>
    public async Task<IndexManifest> BuildAsync(IndexCatalog catalog, string version, IndexSettings settings)
    {
        var chunks = await ChunkAsync(settings);
        var vectors = await new OnnxEmbeddingGenerator(Model(settings.Int8)).GenerateAsync(chunks.Select(c => c.Chunk.IndexText));
        await File.WriteAllLinesAsync(Path.Combine(catalog.PathOf(version), "chunks.jsonl"), chunks.Select((c, i) => JsonSerializer.Serialize(new StoredChunk(
            c.Ordinal, c.Document.Id, c.Collection, c.DocumentType, c.EquipmentTags, c.EffectiveFrom, c.EffectiveTo,
            c.Chunk.Start, c.Chunk.End, c.Chunk.Text, c.Chunk.Header, c.Chunk.Returns?.Start, c.Chunk.Returns?.End, c.Chunk.Kind, vectors[i].Vector.ToArray()), Json)));
        var modelSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(EmbeddingModels.BgeSmall(settings.Int8).ModelPath)))[..12];
        var included = Included(settings).Select(s => s.Entry.Sha256);
        var corpusSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', included))))[..12];
        var manifest = new IndexManifest(version, settings.Chunker, settings.Breadcrumbs, settings.EmbeddingModel, modelSha, chunks.Count, corpusSha, Today);
        catalog.SaveManifest(manifest);
        return manifest;
    }

    /// <summary>
    /// The retriever a service runs over a stored version: Lucene and the stored vectors fused with RRF (a 2-second limit
    /// per source, then keyword-only), re-ranked by MiniLM-L6. A decorator can wrap the dense source, as a fault test does.
    /// </summary>
    public async Task<ServedIndex> LoadAsync(IndexCatalog catalog, string version, RetrievalSettings settings, Func<IRetriever, IRetriever>? wrapDense = null)
    {
        var manifest = catalog.Manifest(version);
        var documents = _sources.ToDictionary(s => s.Document.Id, s => s.Document, StringComparer.Ordinal);
        var stored = File.ReadLines(Path.Combine(catalog.PathOf(version), "chunks.jsonl")).Select(l => JsonSerializer.Deserialize<StoredChunk>(l, Json)!).ToList();
        var chunks = stored.Select(s => new IndexedChunk(s.Ordinal,
            new Chunk(s.DocumentId, s.Start, s.End, s.Text) { Header = s.Header, Kind = s.Kind, Returns = s.ReturnStart is { } rs ? (rs, s.ReturnEnd!.Value) : null },
            documents[s.DocumentId], s.Collection, s.DocumentType, s.EquipmentTags, s.EffectiveFrom, s.EffectiveTo)).ToList();

        var sources = new List<IRetriever>();
        if (settings.Keyword)
        {
            sources.Add(new LuceneRetriever("keyword", chunks, settings.Identifiers ? ContosoAnalyzers.Identifiers() : new StandardAnalyzer(ContosoAnalyzers.Version)));
        }

        if (settings.Dense)
        {
            var byText = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var (chunk, s) in chunks.Zip(stored)) byText[chunk.Chunk.IndexText] = s.Vector; // identical text, identical vector
            var queries = new OnnxEmbeddingGenerator(Model(manifest.EmbeddingModel.EndsWith("int8", StringComparison.Ordinal)), queries: true);
            IRetriever dense = await DenseRetriever.CreateAsync("dense", chunks, new StoredEmbeddings(byText), queries);
            sources.Add(wrapDense?.Invoke(dense) ?? dense);
        }

        var retriever = sources.Count == 1 ? sources[0] : new HybridRetriever("hybrid", sources, new ReciprocalRankFusion(60));
        if (settings.Rerank) retriever = new RerankingRetriever(retriever.Name + ", re-ranked", retriever, new OnnxCrossEncoderReranker(CrossEncoder), candidates: 8);
        if (settings.Graph) retriever = new GraphRoute(await BuildGraphAsync(chunks), chunks, retriever);
        if (!settings.TrimInSearch) retriever = new FilterAfterRetriever(retriever, Everyone);
        return new ServedIndex(version, manifest, retriever, chunks);
    }

    /// <summary>Chapter 19's graph from these chunks' documents, by rules alone (ADR-0014): every edge points back at the chunk it was read from.</summary>
    private async Task<KnowledgeGraph> BuildGraphAsync(IReadOnlyList<IndexedChunk> chunks)
    {
        var (graph, builder) = (new KnowledgeGraph(), new GraphBuilder(GraphRoute.Recognizer));
        var byDocument = chunks.ToLookup(c => c.Document.Id, c => c.Chunk);
        foreach (var source in _sources.Where(s => byDocument.Contains(s.Document.Id)))
        {
            await builder.AddAsync(graph, source.Text, Versions.GetValueOrDefault(source.Document.Id, ""), [.. byDocument[source.Document.Id]], new ExtractionOptions(Selective: true));
        }

        return graph;
    }

    /// <summary>Sufficiency per answerable question: does the top 4 hold every labeled fact (Chapter 5's harness)?</summary>
    public async Task<IReadOnlyDictionary<string, double>> ScoreAsync(IRetriever retriever, IReadOnlyList<IndexedChunk> chunks, IReadOnlyList<GoldenQuestion> questions)
    {
        // The harness checks each hit against the asker's access, so it needs every document the index holds, copies included.
        var harness = new Harness([.. Documents.Values.Concat(chunks.Select(c => c.Document)).DistinctBy(d => d.Id)], GoldenSet.Sha256(GoldenSetPath));
        var run = await harness.RunAsync(new HarnessConfiguration(retriever, chunks), questions, 4, questions[0].Split);
        return run.Answerable.ToDictionary(q => q.Id, q => q.Sufficient);
    }
}

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

/// <summary>Searches as someone who may read everything and drops what the asker can't read afterward: the design Chapter 22 rejected, kept for the ablation.</summary>
public sealed class FilterAfterRetriever(IRetriever inner, Person everyone) : IRetriever
{
    public string Name => inner.Name + ", filtered after";

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var result = await inner.RetrieveAsync(request with { Asker = everyone }, cancellationToken);
        return result with { Hits = [.. result.Hits.Where(h => request.Asker.CanRead(h.Chunk.Document))] };
    }
}

/// <summary>An <see cref="IRetriever"/> as a configuration for Chapter 5's harness.</summary>
public sealed class HarnessConfiguration(IRetriever retriever, IReadOnlyList<IndexedChunk> chunks) : IRetrievalUnderTest
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
