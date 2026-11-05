using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Vision;

namespace Ch18.Knowledge.Web;

/// <summary>A PDF the viewer can show: who may read it, its bytes and its page count.</summary>
public sealed record LibraryPdf(SourceDocument Document, byte[] Bytes, int Pages);

/// <summary>
/// The corpus's PDFs, loaded once. A page is served only to a person who may read its document; for anyone else the
/// page doesn't exist, so a link to a restricted document says nothing about it.
/// </summary>
public sealed class DocumentLibrary
{
    private readonly Lazy<Task<Dictionary<string, LibraryPdf>>> _pdfs = new(LoadAsync);

    public async Task<LibraryPdf?> OpenAsync(string id, int page, string? personId)
    {
        var pdfs = await _pdfs.Value;
        return pdfs.TryGetValue(id, out var pdf) && People.Find(personId ?? "") is { } person && person.CanRead(pdf.Document)
            && page >= 1 && page <= pdf.Pages ? pdf : null;
    }

    private static async Task<Dictionary<string, LibraryPdf>> LoadAsync()
    {
        var pdfs = new Dictionary<string, LibraryPdf>(StringComparer.Ordinal);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.MediaType == MediaTypes.Pdf && e.CopyOf is null))
        {
            var m = (await GeneratedCorpus.TruthAsync(entry)).Metadata;
            var bytes = await File.ReadAllBytesAsync(GeneratedCorpus.FullPath(entry));
            pdfs[entry.Id] = new LibraryPdf(new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, ""), bytes, PageRenders.PageCount(bytes));
        }

        return pdfs;
    }
}
