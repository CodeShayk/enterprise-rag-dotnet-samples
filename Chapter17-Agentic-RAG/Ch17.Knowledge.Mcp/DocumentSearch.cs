using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using Microsoft.ML.Tokenizers;

namespace Ch17.Knowledge.Mcp;

/// <summary>
/// Contoso's document search as the assistant runs it: the hybrid retriever re-ranked by MiniLM-L6, Chapter 14's
/// context assembly (abstaining below a calibrated 0.005) and extractive compression to 500 tokens. The retriever
/// filters by the asker's groups, so the same call returns different sources to different people.
/// </summary>
public sealed class DocumentSearch(IRetriever retriever, ContextAssembler assembler, ExtractiveCompressor? compressor = null)
{
    public async Task<IReadOnlyList<EvidenceBlock>> SearchAsync(string query, Person asker, CancellationToken cancellationToken = default)
    {
        var result = await retriever.RetrieveAsync(new RetrievalRequest(query, asker, 4), cancellationToken);
        var found = assembler.Assemble(result.Hits);
        if (found.IsEmpty) return [];
        return (compressor is null ? found : compressor.Compress(query, found)).Blocks;
    }

    /// <summary>Builds the index from the generated corpus, as Chapters 14 to 17 do (about 15 s on the reference laptop).</summary>
    public static async Task<DocumentSearch> LoadAsync()
    {
        var o200k = TiktokenTokenizer.CreateForEncoding("o200k_base");
        var chunks = new List<IndexedChunk>();
        var chunker = new ParentChildChunker(o200k, 100, 600);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            var document = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                chunks.Add(new IndexedChunk(chunks.Count, chunk, document, Collection(m.DocumentType), m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
            }
        }

        var bge = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);
        var crossEncoder = new OnnxCrossEncoder("MiniLM-L6", Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"),
            Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"), spinning: false);
        var keyword = new LuceneRetriever("keyword", chunks, ContosoAnalyzers.Identifiers());
        var dense = await DenseRetriever.CreateAsync("dense", chunks, new OnnxEmbeddingGenerator(bge), new OnnxEmbeddingGenerator(bge, queries: true));
        var hybrid = new HybridRetriever("hybrid", [keyword, dense], new ReciprocalRankFusion(60));
        return new DocumentSearch(new RerankingRetriever("hybrid, re-ranked", hybrid, new OnnxCrossEncoderReranker(crossEncoder), candidates: 8),
            new ContextAssembler(new Calibration(0.474, -3.543), o200k, new ContextOptions { MaxBlocks = 4 }), new ExtractiveCompressor(crossEncoder, o200k, 500));
    }

    private static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" => "notices and reports",
        _ => "policies and bulletins",
    };
}
