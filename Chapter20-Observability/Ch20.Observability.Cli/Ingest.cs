using System.Diagnostics;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Observability;
using Microsoft.Extensions.AI;

namespace Ch20.Observability.Cli;

/// <summary>Ingestion as one trace: an index span with a parse and a chunk span per document and embed spans per batch.</summary>
public static class Ingest
{
    public static async Task RunAsync()
    {
        using var telemetry = new Telemetry();
        var chunker = new ParentChildChunker(Lab.O200k, 100, 600);
        using var bge = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);
        var passages = new EmbeddingGeneratorBuilder<string, Embedding<float>>(new OnnxEmbeddingGenerator(bge)).UseOpenTelemetry(sourceName: RagTelemetry.ModelsSource).Build();
        var texts = new List<string>();
        using (var index = RagTelemetry.Source.StartActivity("index corpus"))
        {
            foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal) && (e.Id != "SOP-FS-014" || e.Version == "3")))
            {
                ChunkSource source;
                using (var parse = RagTelemetry.Source.StartActivity("parse"))
                {
                    parse?.SetTag("rag.document", entry.Id);
                    parse?.SetTag("rag.document.version", entry.Version);
                    parse?.SetTag("rag.document.media_type", entry.MediaType);
                    var truth = await GeneratedCorpus.TruthAsync(entry);
                    source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
                    parse?.SetTag("rag.document.blocks", truth.Blocks.Count);
                }

                using var chunk = RagTelemetry.Source.StartActivity("chunk");
                var chunks = await chunker.SplitAsync(source);
                chunk?.SetTag("rag.document", entry.Id);
                chunk?.SetTag("rag.chunker", chunker.Name);
                chunk?.SetTag("rag.chunks", chunks.Count);
                texts.AddRange(chunks.Select(c => c.IndexText));
            }

            using var embed = RagTelemetry.Source.StartActivity("embed");
            embed?.SetTag("rag.chunks", texts.Count);
            foreach (var batch in texts.Chunk(32)) await passages.GenerateAsync(batch);
        }

        Console.WriteLine($"{"Span",-30} {"count",6} {"total ms",9} {"max ms",8}");
        foreach (var g in telemetry.Spans.GroupBy(s => s.OperationName).OrderBy(g => g.Min(s => s.StartTimeUtc.Ticks)))
        {
            Console.WriteLine($"{g.Key,-30} {g.Count(),6} {g.Sum(s => s.Duration.TotalMilliseconds),9:F0} {g.Max(s => s.Duration.TotalMilliseconds),8:F1}");
        }

        var slowest = telemetry.Spans.Where(s => s.OperationName == "parse").OrderByDescending(s => s.Duration).First();
        Console.WriteLine($"\nSlowest parse: {slowest.GetTagItem("rag.document")} ({slowest.GetTagItem("rag.document.media_type")}), {slowest.Duration.TotalMilliseconds:F0} ms; {texts.Count} chunks embedded");
    }
}
