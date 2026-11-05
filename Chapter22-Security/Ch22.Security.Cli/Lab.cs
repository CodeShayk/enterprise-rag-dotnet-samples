using System.Collections.Concurrent;
using System.Text.Json;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OllamaSharp;

namespace Ch22.Security.Cli;

/// <summary>An attack from <c>data/attacks.v1.jsonl</c>: a poisoned document and the question that reaches it, or a question that attacks directly.</summary>
public sealed record Attack(
    string Id, string Kind, string? Document, string? Title, string? Type, IReadOnlyList<string>? Groups, IReadOnlyList<string>? Tags,
    string? Text, string Question, string Asker, string Payload);

/// <summary>
/// What every experiment shares, copied from Chapter 21 because chapters don't reference each other: the corpus and its
/// parent-child chunks, golden set v2 and the shipped retriever. New here: indexes rebuilt from any chunk list (with
/// updated permissions or with poisoned documents added) without embedding unchanged text twice.
/// </summary>
public sealed class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public required IReadOnlyList<Attack> Attacks { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static Calibration Calibration { get; } = new(0.474, -3.543);

    public static string Runs { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch22"));

    public IEnumerable<GoldenQuestion> Split(string split) => Questions.Where(q => q.Split == split);

    public static async Task<Lab> LoadAsync()
    {
        var (documents, chunks) = (new Dictionary<string, SourceDocument>(StringComparer.Ordinal), new List<IndexedChunk>());
        var chunker = new ParentChildChunker(O200k, 100, 600);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            var document = documents[entry.Id] = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                chunks.Add(new IndexedChunk(chunks.Count, chunk, document, Collection(m.DocumentType), m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
            }
        }

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var attacks = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "attacks.v1.jsonl")).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<Attack>(l, json)!).ToList();
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        return new Lab { Documents = documents, Chunks = chunks, Questions = GoldenSet.Load(path), Attacks = attacks };
    }

    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" or "upload" => "notices and reports",
        _ => "policies and bulletins",
    };

    /// <summary>The corpus's chunks with an attack's poisoned documents added, each indexed as one chunk.</summary>
    public IReadOnlyList<IndexedChunk> WithPoison(IEnumerable<Attack> attacks)
    {
        var chunks = Chunks.ToList();
        foreach (var a in attacks.Where(a => a.Document is not null))
        {
            var document = new SourceDocument(a.Document!, a.Title!, "corporate", [.. a.Groups!], a.Text!);
            var chunk = new Chunk(document.Id, 0, a.Text!.Length, a.Text) { Header = a.Title };
            chunks.Add(new IndexedChunk(chunks.Count, chunk, document, Collection(a.Type), a.Type!, [.. a.Tags!], null, null));
        }

        return chunks;
    }

    private OnnxCrossEncoder? _crossEncoder;
    private OnnxEmbeddingModel? _bge;
    private CachedEmbeddings? _passages;

    public OnnxCrossEncoder CrossEncoder => _crossEncoder ??= new("MiniLM-L6",
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"), Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);

    public OnnxEmbeddingModel Bge => _bge ??= new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);

    /// <summary>Passage embeddings by text: a rebuild after a permission-only change embeds nothing.</summary>
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
