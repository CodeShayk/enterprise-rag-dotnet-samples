using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OllamaSharp;

namespace Ch14.Knowledge.Api;

/// <summary>
/// The answer pipeline behind the endpoints, built once on first use: the corpus, its parent–child chunks, Chapter 11's
/// hybrid retriever with Chapter 12's re-ranker and calibration, and the model the configuration names. Building the
/// dense index takes several seconds, so it starts when the API does and the first request waits for it.
/// </summary>
public sealed class KnowledgeStack(IConfiguration configuration)
{
    private readonly Lazy<Task<GroundedAnswerGenerator>> _generator = new(() => BuildAsync(configuration));

    public Task<GroundedAnswerGenerator> GeneratorAsync() => _generator.Value;

    private static async Task<GroundedAnswerGenerator> BuildAsync(IConfiguration configuration)
    {
        var o200k = TiktokenTokenizer.CreateForEncoding("o200k_base");
        var chunks = new List<IndexedChunk>();
        var chunker = new ParentChildChunker(o200k, 100, 600);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;   // the archived revision 2
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            var document = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                chunks.Add(new IndexedChunk(chunks.Count, chunk, document, Collection(m.DocumentType), m.DocumentType ?? "", m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
            }
        }

        var bge = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);
        var keyword = new LuceneRetriever("keyword", chunks, ContosoAnalyzers.Identifiers());
        var dense = await DenseRetriever.CreateAsync("dense", chunks, new OnnxEmbeddingGenerator(bge), new OnnxEmbeddingGenerator(bge, queries: true));
        var crossEncoder = new OnnxCrossEncoder("MiniLM-L6",
            Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"),
            Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);
        var retriever = new RerankingRetriever("hybrid, re-ranked",
            new HybridRetriever("hybrid", [keyword, dense], new ReciprocalRankFusion(60)), new OnnxCrossEncoderReranker(crossEncoder), candidates: 8);

        // The configuration Chapter 14 measured and kept: four parents, abstention below 0.005, rows with their column
        // names, and the best sentences up to 500 tokens.
        var assembler = new ContextAssembler(new Calibration(0.474, -3.543), o200k);
        return new GroundedAnswerGenerator(retriever, assembler, Chat(configuration), o200k, compressor: new ExtractiveCompressor(crossEncoder, o200k, 500));
    }

    private static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" => "notices and reports",
        _ => "policies and bulletins",
    };

    /// <summary>The configured model; the scripted one answers the grounding prompt extractively, offline.</summary>
    public static IChatClient Chat(IConfiguration configuration)
    {
        var options = AIClientFactory.ReadOptions(configuration);
        var chat = AIClientFactory.CreateChatClient(options, [ExtractiveScript.Rule, .. ContosoScripts.Default]);
        return options.Provider == AIProvider.Ollama
            ? new ChatClientBuilder(chat).ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 8192)).Build()
            : chat;
    }
}
