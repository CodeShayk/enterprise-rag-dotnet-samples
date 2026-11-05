using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;

namespace Ch09.Chunking.Cli;

/// <summary>
/// The full corpus as the chunkers see it: one current document per logical document (copies, the intranet's copy
/// of the SOP and the archived revision left out), read from the truth each file was rendered from, so parser and
/// OCR errors (Chapters 7 and 8) don't blur the comparison of chunking strategies.
/// </summary>
public sealed class Corpus
{
    public required IReadOnlyList<ChunkSource> Sources { get; init; }

    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyDictionary<string, string> Types { get; init; }

    public static async Task<Corpus> LoadAsync()
    {
        var sources = new List<ChunkSource>();
        var documents = new Dictionary<string, SourceDocument>(StringComparer.Ordinal);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;   // the archived revision 2
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            sources.Add(source);
            documents[entry.Id] = new SourceDocument(entry.Id, truth.Metadata.Title, truth.Metadata.Tenant, truth.Metadata.AllowedGroups, source.Text);
            types[entry.Id] = Collection(truth.Metadata.DocumentType);
        }

        return new Corpus { Sources = sources, Documents = documents, Types = types };
    }

    /// <summary>Document types grouped into the collections a strategy is chosen for.</summary>
    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" => "notices and reports",
        _ => "policies and bulletins",
    };
}
