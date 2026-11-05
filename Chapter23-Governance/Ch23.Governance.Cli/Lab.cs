using System.Collections.Concurrent;
using System.Security.Cryptography;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Governance;
using Contoso.Knowledge.Graph;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OllamaSharp;

namespace Ch23.Governance.Cli;

/// <summary>
/// What every command shares, copied from Chapter 22 because chapters don't reference each other: the corpus with its
/// register entries (version and file hash), parent-child chunks, golden set v2 and the shipped retriever and
/// generator. New here: Contoso's clock (Monday, February 1, 2027) and the model's digest for provenance.
/// </summary>
public sealed class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyDictionary<string, RegisterEntry> Entries { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static Calibration Calibration { get; } = new(0.474, -3.543);

    public static string Runs { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch23"));

    /// <summary>"Today" in all of Contoso's data.</summary>
    public static DateTimeOffset Today { get; } = new(2027, 2, 1, 7, 0, 0, TimeSpan.Zero);

    /// <summary>Every group at once: the identity the privacy office's probes run under, so no copy hides behind a permission.</summary>
    public static Person Everyone { get; } = new("privacy-office", "Privacy office", "", "corporate",
        People.All.SelectMany(p => p.Groups).Concat(["motion", "service", "procurement", "hr", "people-managers"]).ToHashSet(StringComparer.Ordinal));

    public IEnumerable<GoldenQuestion> Split(string split) => Questions.Where(q => q.Split == split);

    public static async Task<Lab> LoadAsync()
    {
        var (documents, entries, chunks) = (new Dictionary<string, SourceDocument>(StringComparer.Ordinal), new Dictionary<string, RegisterEntry>(StringComparer.Ordinal), new List<IndexedChunk>());
        var chunker = new ParentChildChunker(O200k, 100, 600);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            var document = documents[entry.Id] = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            entries[entry.Id] = entry;
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                chunks.Add(new IndexedChunk(chunks.Count, chunk, document, Collection(m.DocumentType), m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        return new Lab { Documents = documents, Entries = entries, Chunks = chunks, Questions = GoldenSet.Load(path) };
    }

    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" or "upload" => "notices and reports",
        _ => "policies and bulletins",
    };

    /// <summary>A version of the index: a hash over every chunk's document, place and text, so any change to any chunk changes it.</summary>
    public static string IndexVersion(IEnumerable<IndexedChunk> chunks) =>
        "idx-" + AnswerProvenanceRecord.Sha(string.Join('\n', chunks.Select(c => $"{c.Document.Id}:{c.Chunk.Start}:{AnswerProvenanceRecord.Sha(c.Chunk.Text)}")))[..12];

    /// <summary>
    /// The digest Ollama reports for a model: the SHA-256 of its manifest, which names every layer's hash. Read from disk,
    /// so provenance doesn't depend on the server being up.
    /// </summary>
    public static string OllamaDigest(string model, string tag = "latest")
    {
        var manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models", "manifests", "registry.ollama.ai", "library", model, tag);
        return File.Exists(manifest) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifest)))[..12] : "unknown";
    }

    private OnnxCrossEncoder? _crossEncoder;
    private OnnxEmbeddingModel? _bge;
    private CachedEmbeddings? _passages;

    public OnnxCrossEncoder CrossEncoder => _crossEncoder ??= new("MiniLM-L6",
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"), Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);

    public OnnxEmbeddingModel Bge => _bge ??= new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);

    /// <summary>Passage embeddings by text: an index rebuilt without one document embeds nothing.</summary>
    public CachedEmbeddings Passages => _passages ??= new CachedEmbeddings(new OnnxEmbeddingGenerator(Bge));

    /// <summary>Chapter 11's hybrid over the given chunks, re-ranked by MiniLM-L6 when asked: the shipped retriever.</summary>
    public async Task<IRetriever> RetrieverAsync(IReadOnlyList<IndexedChunk> chunks, bool rerank = true)
    {
        var keyword = new LuceneRetriever("keyword", chunks, ContosoAnalyzers.Identifiers());
        var dense = await DenseRetriever.CreateAsync("dense", chunks, Passages, new OnnxEmbeddingGenerator(Bge, queries: true));
        var hybrid = new HybridRetriever("hybrid", [keyword, dense], new ReciprocalRankFusion(60));
        return rerank ? new RerankingRetriever("hybrid, re-ranked", hybrid, new OnnxCrossEncoderReranker(CrossEncoder), candidates: 8) : hybrid;
    }

    public GroundedAnswerGenerator Generator(IRetriever retriever, IChatClient chat) =>
        new(retriever, new ContextAssembler(Calibration, O200k, new ContextOptions { MaxBlocks = 4 }), chat, O200k,
            compressor: new ExtractiveCompressor(CrossEncoder, O200k, 500));

    /// <summary>llama3.2 behind the recorder, seeded from Chapter 14's recordings so unchanged prompts replay.</summary>
    public static RecordedChatClient AnswerChat()
    {
        var path = Path.Combine(Runs, "llm-recordings.jsonl");
        var seed = Path.Combine(Runs, "..", "ch14", "llm-recordings.jsonl");
        if (!File.Exists(path) && File.Exists(seed))
        {
            Directory.CreateDirectory(Runs);
            File.Copy(seed, path);
        }

        var live = new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = "llama3.2", OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 8192))
            .Use(inner => new ModelCircuitBreaker(inner, timeout: TimeSpan.FromMinutes(10)))
            .Build();
        return new RecordedChatClient(live, path);
    }

    public static async Task<Answer> AnswerAsync(GroundedAnswerGenerator generator, string question, Person asker)
    {
        Answer? answer = null;
        await foreach (var e in generator.StreamAsync(question, asker))
        {
            if (e is AnswerReady ready) answer = ready.Answer;
        }

        return answer!;
    }

    /// <summary>Contoso's names, copied from Chapter 19: suppliers, customers, sites and people, with the short forms people write.</summary>
    public static EntityRecognizer Recognizer { get; } = new(
    [
        new("Supplier", "Fabrikam Seals", ["Fabrikam"]),
        new("Supplier", "Litware Bearings", ["Litware"]),
        new("Supplier", "Proseware Castings", ["Proseware"]),
        new("Customer", "Tailspin Water Utility", ["Tailspin"]),
        new("Customer", "Woodgrove Foods", ["Woodgrove"]),
        new("Site", "Riverside plant", ["Riverside"]),
        new("Site", "Northgate plant", ["Northgate"]),
        new("Site", "Lakeside plant", ["Lakeside"]),
        new("Site", "Raw-water pumping station 3", []),
        .. People.All.Select(p => new GazetteerEntry("Person", p.Name, [p.Name.Split(' ')[0]])),
    ]);

    /// <summary>Chapter 19's rule-built graph over the corpus: no model calls, every fact with a mention of its document version.</summary>
    public async Task<KnowledgeGraph> GraphAsync()
    {
        var graph = new KnowledgeGraph();
        var builder = new GraphBuilder(Recognizer);
        foreach (var (id, entry) in Entries)
        {
            var truth = await GeneratedCorpus.TruthAsync(entry);
            await builder.AddAsync(graph, new ChunkSource(id, truth.Metadata.Title, truth), entry.Version, [.. Chunks.Where(c => c.Document.Id == id).Select(c => c.Chunk)], new ExtractionOptions());
        }

        return graph;
    }

    public static OnnxNer Ner() => new(Path.Combine(EmbeddingModels.Root, "bert-base-ner"));
}

/// <summary>An embedding generator that remembers every text it has embedded.</summary>
public sealed class CachedEmbeddings(IEmbeddingGenerator<string, Embedding<float>> inner) : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    private readonly ConcurrentDictionary<string, Embedding<float>> _cache = new(StringComparer.Ordinal);

    public int Computed { get; private set; }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var missing = texts.Where(t => !_cache.ContainsKey(t)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var computed = await base.GenerateAsync(missing, options, cancellationToken);
            for (var i = 0; i < missing.Count; i++) _cache[missing[i]] = computed[i];
            Computed += missing.Count;
        }

        return new GeneratedEmbeddings<Embedding<float>>(texts.Select(t => _cache[t]));
    }
}
