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

namespace Ch21.Performance.Cli;

/// <summary>
/// What every experiment shares, copied from Chapter 20 because chapters don't reference each other: the corpus and its
/// parent-child chunks, golden set v2 and Chapter 14's shipped pipeline. New here: either llama3.2 model behind the
/// recorder (the 3B answers seeded from Chapter 14's recordings), and the live clients the timed runs use.
/// </summary>
public sealed class Lab
{
    public const string Large = "llama3.2";

    public const string Small = "llama3.2:1b";

    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static Calibration Calibration { get; } = new(0.474, -3.543);

    public static string Runs { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch21"));

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

        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        return new Lab { Documents = documents, Chunks = chunks, Questions = GoldenSet.Load(path) };
    }

    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" => "notices and reports",
        _ => "policies and bulletins",
    };

    public IReadOnlyList<LocatedSpan> Spans(GoldenQuestion question) => GoldenSet.Locate(question, Documents);

    private OnnxCrossEncoder? _crossEncoder;
    private OnnxEmbeddingModel? _bge;
    private IRetriever? _retriever;

    public OnnxCrossEncoder CrossEncoder => _crossEncoder ??= CreateCrossEncoder();

    public OnnxEmbeddingModel Bge => _bge ??= new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);

    public static OnnxCrossEncoder CreateCrossEncoder(int intraOpThreads = 0) => new("MiniLM-L6",
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"), Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"),
        intraOpThreads: intraOpThreads, spinning: false);

    /// <summary>Chapter 11's hybrid, re-ranked by MiniLM-L6 (Chapter 12): the shipped retriever.</summary>
    public async Task<IRetriever> RetrieverAsync()
    {
        if (_retriever is not null) return _retriever;
        var hybrid = await HybridAsync();
        return _retriever = new RerankingRetriever("hybrid, re-ranked", hybrid, new OnnxCrossEncoderReranker(CrossEncoder), candidates: 8);
    }

    public async Task<IRetriever> HybridAsync()
    {
        var keyword = new LuceneRetriever("keyword", Chunks, ContosoAnalyzers.Identifiers());
        var dense = await DenseRetriever.CreateAsync("dense", Chunks, new OnnxEmbeddingGenerator(Bge), new OnnxEmbeddingGenerator(Bge, queries: true));
        return new HybridRetriever("hybrid", [keyword, dense], new ReciprocalRankFusion(60));
    }

    public async Task<GroundedAnswerGenerator> GeneratorAsync(IChatClient chat) =>
        new(await RetrieverAsync(), new ContextAssembler(Calibration, O200k, new ContextOptions { MaxBlocks = 4 }), chat, O200k,
            compressor: new ExtractiveCompressor(CrossEncoder, O200k, 500));

    /// <summary>
    /// A model behind the recorder: an unchanged call replays with its recorded times. The 3B model's file starts as
    /// a copy of Chapter 14's, so the shipped answers replay; the 1B model's fills as it runs.
    /// </summary>
    public static RecordedChatClient AnswerChat(string model) => model == Large
        ? new RecordedChatClient(LiveChat(model), Seeded("llm-recordings.jsonl", Path.Combine(Runs, "..", "ch14", "llm-recordings.jsonl")))
        : new RecordedChatClient(LiveChat(model), Path.Combine(Runs, $"llm-recordings-{model.Replace(':', '-')}.jsonl"));

    /// <summary>A model called every time, for timed runs: the load test and the spot checks.</summary>
    public static IChatClient LiveChat(string model) =>
        new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = model, OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 8192))
            .Use(inner => new ModelCircuitBreaker(inner, timeout: TimeSpan.FromMinutes(10)))
            .Build();

    public static async Task<Answer> AnswerAsync(GroundedAnswerGenerator generator, GoldenQuestion q, IReadOnlyList<EvidenceBlock>? evidence = null)
    {
        Answer? answer = null;
        await foreach (var e in generator.StreamAsync(q.Question, People.Find(q.Asker)!, evidence: evidence))
        {
            if (e is AnswerReady ready) answer = ready.Answer;
        }

        return answer!;
    }

    private static string Seeded(string file, string seed)
    {
        var path = Path.Combine(Runs, file);
        if (!File.Exists(path) && File.Exists(seed))
        {
            Directory.CreateDirectory(Runs);
            File.Copy(seed, path);
        }

        return path;
    }
}
