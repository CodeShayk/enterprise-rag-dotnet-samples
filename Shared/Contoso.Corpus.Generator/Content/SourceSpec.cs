using Contoso.Corpus.Generator.Writers;
using Contoso.Knowledge.Documents;

namespace Contoso.Corpus.Generator.Content;

internal enum Format { Pdf, Docx, Pptx, Xlsx, Html, Eml, Csv }

/// <summary>
/// One file in a source system: the truth it's rendered from, the format and the habits of the software
/// that wrote it. <see cref="CanonicalDocument.Source"/> holds its path under <c>corpus/sources</c>.
/// </summary>
internal sealed record SourceSpec(CanonicalDocument Truth, Format Format, DateTimeOffset Modified)
{
    public string Author { get; init; } = "Contoso Industrial";

    public PageStyle Page { get; init; } = PageStyle.Default;

    /// <summary>Pages rendered as scans with no text layer; <see langword="null"/> means none.</summary>
    public IReadOnlySet<int>? ScannedPages { get; init; }

    /// <summary>Every page is a scan.</summary>
    public bool ScannedAll { get; init; }

    public ScanStyle Scan { get; init; } = ScanStyle.Default;

    public DocxOptions Docx { get; init; } = new();

    public IReadOnlyList<SlideSpec> Slides { get; init; } = [];

    public IReadOnlyList<SheetSpec> Sheets { get; init; } = [];

    public HtmlPage? Html { get; init; }

    public IReadOnlyList<MailMessageSpec> Thread { get; init; } = [];

    /// <summary>Paths of other files in the corpus attached to this email, byte for byte.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>The CSV's header and rows, for tabular exports.</summary>
    public (IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows)? Csv { get; init; }

    /// <summary>Write the same bytes as the file at this path: an exact duplicate in another folder.</summary>
    public string? CopyOf { get; init; }

    /// <summary>Metadata fields the source system doesn't report for this file, to exercise the data contract (Chapter 6).</summary>
    public IReadOnlyList<string> MissingMetadata { get; init; } = [];

    /// <summary>A note for the manifest: what makes this file interesting.</summary>
    public string? Note { get; init; }

    public string Path => Truth.Source.Uri;
}
