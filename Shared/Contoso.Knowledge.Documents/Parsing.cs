namespace Contoso.Knowledge.Documents;

/// <summary>
/// Turns one file into blocks of the canonical model. Parsers know formats, not Contoso: the pipeline
/// joins their output with the metadata the source system reported (Chapter 6). Chapter 7 implements
/// one parser per format; Chapter 8 adds OCR for pages without a text layer.
/// </summary>
public interface IDocumentParser
{
    /// <summary>A short name such as <c>pdf</c> or <c>docx</c>.</summary>
    string Name { get; }

    /// <summary>Changes whenever the parser's output changes, so that the pipeline knows what to reprocess.</summary>
    string Version { get; }

    /// <summary>The media types this parser reads.</summary>
    IReadOnlyList<string> MediaTypes { get; }

    Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default);
}

/// <summary>What a parser is told about the file it reads.</summary>
public sealed record ParseContext(string FileName, string MediaType)
{
    public ParserLimits Limits { get; init; } = ParserLimits.Default;
}

/// <summary>Resource limits every parser enforces, because input files are untrusted (Chapter 7).</summary>
public sealed record ParserLimits
{
    public static ParserLimits Default { get; } = new();

    public long MaxInputBytes { get; init; } = 50 * 1024 * 1024;

    /// <summary>The most bytes an Office or other zip-based file may expand to, which stops zip bombs.</summary>
    public long MaxExpandedBytes { get; init; } = 200 * 1024 * 1024;

    public int MaxPages { get; init; } = 2_000;

    /// <summary>How deep attachments may nest (an email with a zip with an email...).</summary>
    public int MaxAttachmentDepth { get; init; } = 3;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>A parser's output: blocks in reading order plus what it learned about the file itself.</summary>
public sealed record ParsedContent(IReadOnlyList<Block> Blocks)
{
    public IReadOnlyList<PageInfo> Pages { get; init; } = [];

    /// <summary>The title stored in the file, if any (document properties, the HTML title, the PDF info dictionary).</summary>
    public string? Title { get; init; }

    /// <summary>File-level properties found while parsing, such as an author or a modified date.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>Problems that didn't stop the parse but should be reviewed, such as a page without a text layer.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Files inside this one (email attachments, embedded objects) for the pipeline to parse in turn.</summary>
    public IReadOnlyList<EmbeddedFile> Attachments { get; init; } = [];
}

/// <summary>A file found inside another one.</summary>
public sealed record EmbeddedFile(string FileName, string MediaType, byte[] Content);

/// <summary>The media types the corpus uses.</summary>
public static class MediaTypes
{
    public const string Pdf = "application/pdf";
    public const string Html = "text/html";
    public const string Markdown = "text/markdown";
    public const string PlainText = "text/plain";
    public const string Csv = "text/csv";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Eml = "message/rfc822";
    public const string Msg = "application/vnd.ms-outlook";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = Pdf, [".html"] = Html, [".htm"] = Html, [".md"] = Markdown, [".txt"] = PlainText,
        [".csv"] = Csv, [".docx"] = Docx, [".pptx"] = Pptx, [".xlsx"] = Xlsx, [".eml"] = Eml,
        [".msg"] = Msg, [".png"] = Png, [".jpg"] = Jpeg, [".jpeg"] = Jpeg,
    };

    /// <summary>The media type for a file name's extension, or <c>application/octet-stream</c>.</summary>
    public static string FromFileName(string fileName) =>
        ByExtension.GetValueOrDefault(Path.GetExtension(fileName), "application/octet-stream");
}
