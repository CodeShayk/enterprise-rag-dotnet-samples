using CommunityToolkit.VectorData.InMemory;
using Contoso.AI;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Parsing;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;

namespace Ch06.Ingestion.Worker;

/// <summary>
/// Chapter 6's stages on Microsoft.Extensions.DataIngestion (a preview): our parsers behind its
/// <see cref="IngestionDocumentReader"/>, its <see cref="HeaderChunker"/> and its <see cref="VectorStoreWriter{T}"/> over
/// Microsoft.Extensions.VectorData. The building blocks line up with the pipeline's stages; change detection,
/// versions, duplicates, lineage and quarantine stay the pipeline's job.
/// </summary>
public static class DataIngestionMapping
{
    public static async Task RunAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        var embeddings = new HashingEmbeddingGenerator();
        var vectorStore = new InMemoryVectorStore(new InMemoryVectorStoreOptions { EmbeddingGenerator = embeddings });
        using var writer = new VectorStoreWriter<string>(vectorStore, HashingEmbeddingGenerator.DefaultDimensions,
            new VectorStoreWriterOptions { CollectionName = "contoso_ch06" });
        using var pipeline = new IngestionPipeline<string>(
            new CanonicalDocumentReader(ParserRegistry.Default),
            new HeaderChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 200 }),
            writer,
            new IngestionPipelineOptions(),
            loggerFactory: null);

        var files = new DirectoryInfo(Path.Combine(workspace.Sources, "fileshare", "fluid", "sops")).GetFiles("*.docx");
        await foreach (var result in pipeline.ProcessAsync(files, cancellationToken))
        {
            Console.WriteLine($"{(result.Succeeded ? "ingested" : "failed"),-9} {result.DocumentId}{(result.Exception is { } e ? $": {e.Message}" : "")}");
        }

        var question = "How tight should the seal gland nuts be?";
        Console.WriteLine($"\nSearch \"{question}\" in collection contoso_ch06:");
        await foreach (var hit in writer.VectorStoreCollection.SearchAsync(question, 2, cancellationToken: cancellationToken))
        {
            var content = hit.Record.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
            Console.WriteLine($"  {hit.Score:F3}  {content.ReplaceLineEndings(" ")[..Math.Min(100, content.Length)]}");
        }
    }

    /// <summary>Our format parsers as a DataIngestion reader: canonical blocks become its headers, paragraphs and tables.</summary>
    private sealed class CanonicalDocumentReader(ParserRegistry parsers) : IngestionDocumentReader
    {
        public override async Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
        {
            var content = await parsers.ParseAsync(source, new ParseContext(identifier, mediaType), cancellationToken);
            var document = new IngestionDocument(identifier);
            var section = new IngestionDocumentSection();
            foreach (var block in content.Blocks.Where(b => b.Role is null))
            {
                IngestionDocumentElement element = block switch
                {
                    HeadingBlock h => new IngestionDocumentHeader(h.ToMarkdown()) { Level = h.Level, Text = h.Text },
                    _ => new IngestionDocumentParagraph(block.ToMarkdown()) { Text = block.ToMarkdown() },
                };
                element.PageNumber = block.Anchor?.Page;
                section.Elements.Add(element);
            }

            document.Sections.Add(section);
            return document;
        }
    }
}
