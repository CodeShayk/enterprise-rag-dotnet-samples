using System.Text.Json;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Graph;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OllamaSharp;

namespace Ch19.GraphRAG.Cli;

/// <summary>A multi-hop question: who asks, and the facts its evidence must hold and its answer must state (regular expressions).</summary>
public sealed record MultiHopQuestion(string Id, string Split, string Asker, string Question, IReadOnlyList<Fact> Facts);

public sealed record Fact(string Evidence, string Answer);

/// <summary>A labeled edge: what one document of the evaluation set states.</summary>
public sealed record TruthEdge(string Doc, string From, string Relation, string To);

/// <summary>
/// What every experiment shares, copied from Chapter 18 because chapters don't reference each other: the corpus and
/// its parent-child chunks, the hybrid retriever re-ranked by MiniLM-L6 and Chapter 14's generator with llama3.2,
/// every model call recorded. New here: document versions for provenance, Contoso's gazetteer, the NER model and the
/// question and truth files.
/// </summary>
public sealed class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyDictionary<string, ChunkSource> Sources { get; init; }

    public required IReadOnlyDictionary<string, string> Versions { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static Calibration Calibration { get; } = new(0.474, -3.543);

    public static string Runs { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch19"));

    public static ParentChildChunker Chunker { get; } = new(O200k, 100, 600);

    public static async Task<Lab> LoadAsync()
    {
        var (documents, sources, versions, chunks) = (new Dictionary<string, SourceDocument>(StringComparer.Ordinal), new Dictionary<string, ChunkSource>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal), new List<IndexedChunk>());
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;
            var (source, document) = await OpenAsync(entry);
            (sources[entry.Id], documents[entry.Id], versions[entry.Id]) = (source, document, entry.Version);
            foreach (var chunk in await Chunker.SplitAsync(source)) chunks.Add(Indexed(chunks.Count, chunk, document, source.Document.Metadata));
        }

        return new Lab { Documents = documents, Sources = sources, Versions = versions, Chunks = chunks };
    }

    public static async Task<(ChunkSource Source, SourceDocument Document)> OpenAsync(RegisterEntry entry)
    {
        var truth = await GeneratedCorpus.TruthAsync(entry);
        var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
        var m = truth.Metadata;
        return (source, new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text));
    }

    public static IndexedChunk Indexed(int ordinal, Chunk chunk, SourceDocument document, DocumentMetadata m) =>
        new(ordinal, chunk, document, Collection(m.DocumentType), m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo);

    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" => "notices and reports",
        _ => "policies and bulletins",
    };

    public IReadOnlyList<Chunk> ChunksOf(string id) => [.. Chunks.Where(c => c.Document.Id == id).Select(c => c.Chunk)];

    /// <summary>Contoso's names: suppliers, customers, sites and people, with the short forms people write.</summary>
    public static IReadOnlyList<GazetteerEntry> Gazetteer { get; } =
    [
        new("Supplier", "Fabrikam Seals", ["Fabrikam"]),
        new("Supplier", "Litware Bearings", ["Litware"]),
        new("Supplier", "Proseware Castings", ["Proseware"]),
        new("Supplier", "Contoso stores", []),
        new("Customer", "Tailspin Water Utility", ["Tailspin"]),
        new("Customer", "Woodgrove Foods", ["Woodgrove"]),
        new("Site", "Riverside plant", ["Riverside"]),
        new("Site", "Northgate plant", ["Northgate"]),
        new("Site", "Lakeside plant", ["Lakeside"]),
        new("Site", "Eastfield plant", ["Eastfield"]),
        new("Site", "Raw-water pumping station 3", []),
        new("Site", "Treated-water pumping station 1", []),
        new("Site", "Packaging line 2", []),
        new("Site", "Cold store 1", []),
        .. People.All.Select(p => new GazetteerEntry("Person", p.Name, [p.Name.Split(' ')[0]])),
    ];

    public static EntityRecognizer Recognizer { get; } = new(Gazetteer);

    public static IReadOnlyList<MultiHopQuestion> Questions { get; } = Read<MultiHopQuestion>("multihop.v1.jsonl");

    public static IReadOnlyList<TruthEdge> Truth { get; } = Read<TruthEdge>("graph-truth.v1.jsonl");

    private static IReadOnlyList<T> Read<T>(string file) =>
        [.. File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", file)).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<T>(l, JsonSerializerOptions.Web)!)];

    private OnnxCrossEncoder? _crossEncoder;

    public OnnxCrossEncoder CrossEncoder => _crossEncoder ??= new("MiniLM-L6",
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"), Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);

    private (LuceneRetriever Keyword, DenseRetriever Dense)? _sources;

    /// <summary>Chapter 11's keyword and dense retrievers, built once.</summary>
    public async Task<(LuceneRetriever Keyword, DenseRetriever Dense)> SourcesAsync()
    {
        if (_sources is { } built) return built;
        var bge = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);
        var keyword = new LuceneRetriever("keyword", Chunks, ContosoAnalyzers.Identifiers());
        var dense = await DenseRetriever.CreateAsync("dense", Chunks, new OnnxEmbeddingGenerator(bge), new OnnxEmbeddingGenerator(bge, queries: true));
        return (_sources = (keyword, dense)).Value;
    }

    /// <summary>
    /// The retrievers compared. Chapter 14's hybrid is re-ranked by MiniLM-L6, which keeps four of the top eight
    /// (ADR-0007). The graph's passages are not: a passage that bridges two hops (the catalog row that names a seal's
    /// supplier) looks unrelated to the question, and a re-ranker drops it. Paths plus passages takes the graph's first
    /// two and fills the rest from the re-ranked hybrid.
    /// </summary>
    public async Task<IRetriever> RetrieverAsync(string configuration, KnowledgeGraph graph)
    {
        var (keyword, dense) = await SourcesAsync();
        var hybrid = new RerankingRetriever("hybrid, re-ranked", new HybridRetriever("hybrid", [keyword, dense], new ReciprocalRankFusion(60)), new OnnxCrossEncoderReranker(CrossEncoder), candidates: 8);
        return configuration switch
        {
            "hybrid" => hybrid,
            "graph, one round" => new GraphRetriever(graph, Recognizer, Chunks, rounds: 1),
            "graph" => new GraphRetriever(graph, Recognizer, Chunks),
            "graph, re-ranked" => new RerankingRetriever("graph, re-ranked", new GraphRetriever(graph, Recognizer, Chunks), new OnnxCrossEncoderReranker(CrossEncoder), candidates: 8),
            "hybrid+graph" => new PathsFirstRetriever(new GraphRetriever(graph, Recognizer, Chunks), hybrid),
            _ => throw new ArgumentException(configuration),
        };
    }

    public async Task<GroundedAnswerGenerator> GeneratorAsync(IRetriever retriever, IChatClient chat) =>
        new(retriever, new ContextAssembler(Calibration, O200k, new ContextOptions { MaxBlocks = 4 }), chat, O200k, compressor: new ExtractiveCompressor(CrossEncoder, O200k, 500));

    /// <summary>Answers recorded as text, seeded from Chapter 14's recordings, so an unchanged single pass replays.</summary>
    public static RecordedChatClient AnswerChat() => new(LiveChat("llama3.2"), Seeded("llm-recordings.jsonl", Path.Combine(Runs, "..", "ch14", "llm-recordings.jsonl")));

    /// <summary>Relation extraction and community summaries, recorded on their own.</summary>
    public static RecordedChatClient GraphChat() => new(LiveChat("llama3.2"), Path.Combine(Runs, "graph-recordings.jsonl"));

    /// <summary>An Ollama model on the CPU behind Chapter 16's circuit breaker, with a context window of 8,192 tokens.</summary>
    public static IChatClient LiveChat(string model) =>
        new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = model, OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 8192))
            .Use(inner => new ModelCircuitBreaker(inner, timeout: TimeSpan.FromMinutes(10)))
            .Build();

    public static OnnxNer Ner() => new(Path.Combine(EmbeddingModels.Root, "bert-base-ner"));

    public static OnnxEmbeddingModel Bge() => new(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);

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

/// <summary>Paths plus passages: the graph's first passages, then the re-ranked hybrid's, without repeating a parent.</summary>
public sealed class PathsFirstRetriever(IRetriever graph, IRetriever passages, int fromGraph = 2) : IRetriever
{
    public string Name => "hybrid+graph";

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var paths = (await graph.RetrieveAsync(request with { Top = fromGraph }, cancellationToken)).Hits;
        var rest = (await passages.RetrieveAsync(request, cancellationToken)).Hits;
        return RetrievalResult.Of(Ranking.DistinctReturns([.. paths, .. rest], request.Top));
    }
}
