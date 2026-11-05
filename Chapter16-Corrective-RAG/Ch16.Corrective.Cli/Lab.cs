using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OllamaSharp;

namespace Ch16.Corrective.Cli;

/// <summary>
/// What every experiment shares, copied from Chapter 14 because chapters don't reference each other: the corpus and
/// its parent–child chunks, golden set v2, the hybrid retriever re-ranked by MiniLM-L6 with Chapter 12's calibration,
/// Chapter 14's shipped generator (best sentences, 500 tokens) and llama3.2 through Ollama with every call recorded.
/// The recordings start as a copy of Chapter 14's, so a single pass that hasn't changed replays instead of running.
/// </summary>
public sealed class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public required string Split { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    /// <summary>Chapter 12's Platt scaling of the MiniLM-L6 logits, fitted on dev candidates.</summary>
    public static Calibration Calibration { get; } = new(0.474, -3.543);

    public static string Runs { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch16"));

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

        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        return new Lab { Documents = documents, Chunks = chunks, Questions = [.. GoldenSet.Load(path).Where(q => split == "all" || q.Split == split)], Split = split };
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

    /// <summary>bge-small INT8, shared by the dense retriever and Chapter 13's rewriter guard (passage side, as there).</summary>
    public static OnnxEmbeddingModel Bge() => new(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);

    /// <summary>Chapter 11's hybrid re-ranked by MiniLM-L6, which keeps four of the top eight (ADR-0007).</summary>
    public async Task<IRetriever> RetrieverAsync(OnnxCrossEncoder crossEncoder, OnnxEmbeddingModel bge)
    {
        var keyword = new LuceneRetriever("keyword", Chunks, ContosoAnalyzers.Identifiers());
        var dense = await DenseRetriever.CreateAsync("dense", Chunks, new OnnxEmbeddingGenerator(bge), new OnnxEmbeddingGenerator(bge, queries: true));
        var hybrid = new HybridRetriever("hybrid", [keyword, dense], new ReciprocalRankFusion(60));
        return new RerankingRetriever("hybrid, re-ranked", hybrid, new OnnxCrossEncoderReranker(crossEncoder), candidates: 8);
    }

    public static OnnxCrossEncoder CrossEncoder() => new("MiniLM-L6",
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"),
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);

    /// <summary>Chapter 15's support detector: the 3-way xsmall NLI model, best of block and windows, at 0.05.</summary>
    public static OnnxNliModel Nli() => new("DeBERTa-v3 xsmall, 3-way", Path.Combine(EmbeddingModels.Root, "nli-deberta-v3-xsmall"), ["contradiction", "entailment", "neutral"]);

    /// <summary>llama3.2 recorded in runs/ch16, starting from a copy of Chapter 14's recordings.</summary>
    public static RecordedChatClient Chat() => new(LiveChat(), Seeded("llm-recordings.jsonl", Path.Combine(Runs, "..", "ch14", "llm-recordings.jsonl")));

    /// <summary>Chapter 13's cache of rewrites and decompositions, copied the same way.</summary>
    public static LlmCache Cache() => new(Seeded("llm-cache.jsonl", Path.Combine(Runs, "..", "ch13", "llm-cache.jsonl")));

    /// <summary>llama3.2 through Ollama behind a circuit breaker: after three failures in a row, calls fail at once for 30 s.</summary>
    public static IChatClient LiveChat() =>
        new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = "llama3.2", OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 8192))
            .Use(inner => new ModelCircuitBreaker(inner))
            .Build();

    public static IEscalationQueue Escalations(string config, string split) => new JsonlEscalationQueue(Path.Combine(Runs, "escalations", $"{config}-{split}.jsonl"));

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

    public static string Signed(double x) => (Math.Round(x, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("+0.00;-0.00");

    public static string Interval(Difference d) => $"{Signed(d.Mean)} [{Signed(d.Low)}, {Signed(d.High)}]";
}
