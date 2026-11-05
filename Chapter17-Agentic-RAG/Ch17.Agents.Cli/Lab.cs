using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Agents;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OllamaSharp;

namespace Ch17.Agents.Cli;

/// <summary>
/// What every experiment shares, copied from Chapter 16 because chapters don't reference each other: the corpus and
/// its parent–child chunks, the hybrid retriever re-ranked by MiniLM-L6, Chapter 14's generator (four parents,
/// 500 tokens) and llama3.2 through Ollama behind a circuit breaker. New here: the records database, the agent task
/// set and two more recordings, one for tool-calling turns and one for Text-to-SQL.
/// </summary>
public sealed class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<AgentTask> Tasks { get; init; }

    public required string Split { get; init; }

    public required RecordsDatabase Records { get; init; }

    public required IRetriever Retriever { get; init; }

    public required OnnxCrossEncoder CrossEncoder { get; init; }

    public required OnnxEmbeddingModel Bge { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    /// <summary>Chapter 12's Platt scaling of the MiniLM-L6 logits, fitted on dev candidates.</summary>
    public static Calibration Calibration { get; } = new(0.474, -3.543);

    public static string Corpus { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "corpus"));

    public static string Runs { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch17"));

    public static string TaskFile { get; } = Path.Combine(AppContext.BaseDirectory, "tasks", "agent-tasks.v1.jsonl");

    public static async Task<Lab> LoadAsync(string split)
    {
        var documents = new Dictionary<string, SourceDocument>(StringComparer.Ordinal);
        var chunks = new List<IndexedChunk>();
        var chunker = new ParentChildChunker(O200k, 100, 600);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;   // the archived revision 2
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            var document = documents[entry.Id] = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                chunks.Add(new IndexedChunk(chunks.Count, chunk, document, Collection(m.DocumentType), m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
            }
        }

        var (crossEncoder, bge) = (LoadCrossEncoder(), LoadBge());
        var keyword = new LuceneRetriever("keyword", chunks, ContosoAnalyzers.Identifiers());
        var dense = await DenseRetriever.CreateAsync("dense", chunks, new OnnxEmbeddingGenerator(bge), new OnnxEmbeddingGenerator(bge, queries: true));
        var hybrid = new HybridRetriever("hybrid", [keyword, dense], new ReciprocalRankFusion(60));
        return new Lab
        {
            Documents = documents,
            Chunks = chunks,
            Tasks = [.. AgentTask.Load(TaskFile).Where(t => split == "all" || t.Split == split)],
            Split = split,
            Records = await RecordsAsync(),
            Retriever = new RerankingRetriever("hybrid, re-ranked", hybrid, new OnnxCrossEncoderReranker(crossEncoder), candidates: 8),
            CrossEncoder = crossEncoder,
            Bge = bge,
        };
    }

    /// <summary>The records database, rebuilt from the corpus's work-order export and parts workbook when either is newer.</summary>
    public static async Task<RecordsDatabase> RecordsAsync()
    {
        var (path, csv, workbook) = (Path.Combine(Runs, "records.duckdb"), Path.Combine(Corpus, "sources", "database", "work-orders.csv"),
            Path.Combine(Corpus, "sources", "fileshare", "procurement", "Parts Catalog 2027.xlsx"));
        var stale = !File.Exists(path) || File.GetLastWriteTimeUtc(path) < new[] { csv, workbook }.Max(File.GetLastWriteTimeUtc);
        return stale ? await RecordsDatabase.BuildAsync(path, csv, workbook) : new RecordsDatabase(path);
    }

    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" => "notices and reports",
        _ => "policies and bulletins",
    };

    public ContextAssembler Assembler() => new(Calibration, O200k, new ContextOptions { MaxBlocks = 4 });

    public ExtractiveCompressor Compressor() => new(CrossEncoder, O200k, 500);

    public GroundedAnswerGenerator Generator(IChatClient chat) => new(Retriever, Assembler(), chat, O200k, compressor: Compressor());

    /// <summary>A request's context: the asker, their records session (views with only their rows) and their tool policy.</summary>
    public AgentContext Context(Person asker, bool semantic = true) => new(asker, Records.Open(asker, semantic), ToolPolicy.For(asker));

    public ContosoTools Tools(AgentContext context) => new(context, Retriever, Assembler(), Compressor());

    /// <summary>bge-small INT8, for the dense retriever, the table selector and the router.</summary>
    public static OnnxEmbeddingModel LoadBge() => new(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);

    public static OnnxCrossEncoder LoadCrossEncoder() => new("MiniLM-L6",
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"),
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);

    /// <summary>Answers, recorded as text and seeded from Chapter 14's recordings, so an unchanged single pass replays.</summary>
    public static RecordedChatClient AnswerChat() => new(LiveChat(), Seeded("llm-recordings.jsonl", Path.Combine(Runs, "..", "ch14", "llm-recordings.jsonl")));

    /// <summary>Tool-calling turns, recorded with their function calls.</summary>
    public static RecordedToolChatClient ToolChat() => new(LiveChat(), Path.Combine(Runs, "tool-recordings.jsonl"));

    /// <summary>Text-to-SQL calls, by prompt.</summary>
    public static LlmCache Cache() => new(Path.Combine(Runs, "llm-cache.jsonl"));

    /// <summary>llama3.2 through Ollama behind Chapter 16's circuit breaker.</summary>
    public static IChatClient LiveChat() =>
        new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = "llama3.2", OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 8192))
            .Use(inner => new ModelCircuitBreaker(inner))
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
