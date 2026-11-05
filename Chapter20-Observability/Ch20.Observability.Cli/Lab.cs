using System.Diagnostics;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Observability;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OllamaSharp;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ch20.Observability.Cli;

/// <summary>
/// What every experiment shares, copied from Chapter 19 because chapters don't reference each other: the corpus and
/// its parent-child chunks, golden set v2, Chapter 14's pipeline with every model call recorded. New here: each stage
/// wrapped or instrumented for OpenTelemetry, and the tenant of each person.
/// </summary>
public sealed class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyDictionary<string, ChunkSource> Sources { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static Calibration Calibration { get; } = new(0.474, -3.543);

    public static string Runs { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch20"));

    public static async Task<Lab> LoadAsync()
    {
        var (documents, sources, chunks) = (new Dictionary<string, SourceDocument>(StringComparer.Ordinal), new Dictionary<string, ChunkSource>(StringComparer.Ordinal), new List<IndexedChunk>());
        var chunker = new ParentChildChunker(O200k, 100, 600);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = sources[entry.Id] = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            var document = documents[entry.Id] = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                chunks.Add(new IndexedChunk(chunks.Count, chunk, document, Collection(m.DocumentType), m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        return new Lab { Documents = documents, Sources = sources, Chunks = chunks, Questions = GoldenSet.Load(path) };
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
    private IRetriever? _retriever;

    public OnnxCrossEncoder CrossEncoder => _crossEncoder ??= new("MiniLM-L6",
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"), Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);

    /// <summary>
    /// Chapter 11's hybrid re-ranked by MiniLM-L6, with every stage traced: each retriever and the re-ranker through
    /// decorators, the query embedding through Microsoft.Extensions.AI's GenAI telemetry.
    /// </summary>
    public async Task<IRetriever> RetrieverAsync()
    {
        if (_retriever is not null) return _retriever;
        var bge = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);
        var queries = new EmbeddingGeneratorBuilder<string, Embedding<float>>(new OnnxEmbeddingGenerator(bge, queries: true))
            .UseOpenTelemetry(sourceName: RagTelemetry.ModelsSource)
            .Build();
        var keyword = new TracedRetriever(new LuceneRetriever("keyword", Chunks, ContosoAnalyzers.Identifiers()));
        var dense = new TracedRetriever(await DenseRetriever.CreateAsync("dense", Chunks, new OnnxEmbeddingGenerator(bge), queries));
        var hybrid = new TracedRetriever(new HybridRetriever("hybrid", [keyword, dense], new ReciprocalRankFusion(60)));
        return _retriever = new TracedRetriever(new RerankingRetriever("hybrid, re-ranked", hybrid, new TracedReranker(new OnnxCrossEncoderReranker(CrossEncoder)), candidates: 8));
    }

    public async Task<GroundedAnswerGenerator> GeneratorAsync(IChatClient chat, bool compress = true) =>
        new(await RetrieverAsync(), new ContextAssembler(Calibration, O200k, new ContextOptions { MaxBlocks = 4 }), chat, O200k,
            compressor: compress ? new ExtractiveCompressor(CrossEncoder, O200k, 500) : null);

    /// <summary>
    /// llama3.2 with each call recorded (seeded from Chapter 14's recordings, so an unchanged answer replays) and traced
    /// under the GenAI conventions, content capture off.
    /// </summary>
    public static IChatClient AnswerChat(bool sensitive = false) =>
        new ChatClientBuilder(new RecordedChatClient(LiveChat("llama3.2"), Seeded("llm-recordings.jsonl", Path.Combine(Runs, "..", "ch14", "llm-recordings.jsonl"))))
            .UseOpenTelemetry(sourceName: RagTelemetry.ModelsSource, configure: c => c.EnableSensitiveData = sensitive)
            .Build();

    public static IChatClient LiveChat(string model) =>
        new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = model, OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 8192))
            .Use(inner => new ModelCircuitBreaker(inner, timeout: TimeSpan.FromMinutes(10)))
            .Build();

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

/// <summary>
/// The OpenTelemetry SDK for the experiments: the pipeline's sources and meters, baggage copied onto spans, an
/// in-memory exporter the commands read, and an OTLP exporter as well when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
/// </summary>
public sealed class Telemetry : IDisposable
{
    public List<Activity> Spans { get; } = [];

    public List<Metric> Metrics { get; } = [];

    public TracerProvider Tracer { get; }

    public MeterProvider Meters { get; }

    public Telemetry(Action<TracerProviderBuilder>? traces = null, Action<MeterProviderBuilder>? metrics = null)
    {
        var resource = ResourceBuilder.CreateDefault().AddService("contoso-knowledge", serviceVersion: "ch20");
        var otlp = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 };
        var t = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource)
            .AddSource(RagTelemetry.Name, GenerationTelemetry.SourceName, RagTelemetry.ModelsSource)
            .AddProcessor(new BaggageTagProcessor("contoso.tenant", "user.pseudonym"))
            .AddInMemoryExporter(Spans);
        if (otlp) t.AddOtlpExporter();
        traces?.Invoke(t);
        Tracer = t.Build()!;
        var m = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource)
            .AddMeter(RagTelemetry.Name, RagTelemetry.ModelsSource)
            .AddInMemoryExporter(Metrics);
        if (otlp) m.AddOtlpExporter();
        metrics?.Invoke(m);
        Meters = m.Build()!;
    }

    /// <summary>The sum of a counter's points whose tags match, after a flush.</summary>
    public long Sum(string name, Func<IReadOnlyDictionary<string, object?>, bool>? where = null)
    {
        Meters.ForceFlush();
        long total = 0;
        foreach (var metric in Metrics.Where(m => m.Name == name).TakeLast(1))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, object?>();
                foreach (var tag in point.Tags) tags[tag.Key] = tag.Value;
                if (where is null || where(tags)) total += point.GetSumLong();
            }
        }

        return total;
    }

    public void Dispose()
    {
        Tracer.Dispose();
        Meters.Dispose();
    }
}
