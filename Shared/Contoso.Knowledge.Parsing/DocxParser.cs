using Contoso.Knowledge.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Block = Contoso.Knowledge.Documents.Block;

namespace Contoso.Knowledge.Parsing;

public sealed record DocxParserOptions
{
    /// <summary>
    /// Take each paragraph's <c>InnerText</c>, as a quick integration would. That includes deleted text from
    /// tracked changes and leaves out the list numbers Word generates. For comparison only.
    /// </summary>
    public bool Naive { get; init; }
}

/// <summary>
/// Word documents through the Open XML SDK: heading levels from styles and outline levels, list numbers rebuilt
/// from the numbering definitions, tracked changes accepted (insertions kept, deletions dropped), tables with
/// their header rows, comments, running headers and footers, and the file's core and custom properties.
/// </summary>
public sealed class DocxParser(DocxParserOptions? options = null) : IDocumentParser
{
    private readonly DocxParserOptions _options = options ?? new DocxParserOptions();

    public string Name => "docx";

    public string Version => _options.Naive ? "1.0-naive" : "1.0";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Docx];

    public Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        // MaxCharactersInPart caps how much XML one part may expand to: defense in depth after the zip check.
        using var doc = WordprocessingDocument.Open(content, false, new OpenSettings { MaxCharactersInPart = context.Limits.MaxExpandedBytes / 2 });
        var main = doc.MainDocumentPart ?? throw new InvalidDataException($"{context.FileName} has no document body.");
        var headings = HeadingLevels(main);
        var numbering = new ListNumbering(main);
        var blocks = new List<Block>();

        foreach (var part in main.HeaderParts)
        {
            AddParagraphs(part.Header?.Elements<Paragraph>(), blocks, BlockRoles.Header);
        }

        string? pendingCaption = null;
        foreach (var element in main.Document?.Body?.Elements() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is Paragraph paragraph)
            {
                var text = Text(paragraph);
                if (text.Length == 0) continue;
                var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                if (style == "Caption")
                {
                    pendingCaption = text;   // a caption paragraph belongs to the table after it
                    continue;
                }

                if (pendingCaption is not null)
                {
                    blocks.Add(new ParagraphBlock(pendingCaption));
                    pendingCaption = null;
                }

                if (style is not null && headings.TryGetValue(style, out var level))
                {
                    blocks.Add(new HeadingBlock(level, text));
                }
                else if (paragraph.ParagraphProperties?.NumberingProperties is { } numbered && !_options.Naive)
                {
                    var (marker, depth) = numbering.Next(numbered);
                    blocks.Add(new ListItemBlock(text, depth, marker));
                }
                else
                {
                    blocks.Add(new ParagraphBlock(text));
                }
            }
            else if (element is Table table)
            {
                blocks.Add(new TableBlock(Rows(table), Math.Max(1, table.Elements<TableRow>().Count(r => r.TableRowProperties?.GetFirstChild<TableHeader>() is not null)))
                {
                    Caption = pendingCaption,
                });
                pendingCaption = null;
            }
        }

        foreach (var part in main.FooterParts)
        {
            AddParagraphs(part.Footer?.Elements<Paragraph>(), blocks, BlockRoles.Footer);
        }

        foreach (var comment in main.WordprocessingCommentsPart?.Comments?.Elements<Comment>() ?? [])
        {
            blocks.Add(new ParagraphBlock(TextCleanup.Normalize(comment.InnerText))
            {
                Role = BlockRoles.Comment,
                Anchor = new SourceAnchor { Path = $"comment by {comment.Author?.Value}" },
            });
        }

        // Files embedded in the document (a spreadsheet in a procedure, say) are returned for the pipeline to parse in turn.
        var attachments = main.EmbeddedPackageParts.Select(part =>
        {
            using var stream = part.GetStream();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return new EmbeddedFile(Path.GetFileName(part.Uri.OriginalString), part.ContentType, copy.ToArray());
        }).ToList();

        return Task.FromResult(new ParsedContent(blocks)
        {
            Title = doc.PackageProperties.Title,
            Properties = Properties(doc),
            Attachments = attachments,
        });
    }

    /// <summary>The accepted text of a paragraph: <c>w:t</c> elements only, so deleted text (<c>w:delText</c>) and field codes stay out.</summary>
    private string Text(OpenXmlElement paragraph)
    {
        if (_options.Naive) return TextCleanup.Normalize(paragraph.InnerText);
        var parts = paragraph.Descendants().Select(e => e switch
        {
            Text t when t.Ancestors<MoveFromRun>().Any() => "",
            Text t => t.Text,
            TabChar or Break => " ",
            _ => "",
        });
        return TextCleanup.Normalize(string.Concat(parts));
    }

    private void AddParagraphs(IEnumerable<Paragraph>? paragraphs, List<Block> blocks, string role)
    {
        foreach (var p in paragraphs ?? [])
        {
            var text = Text(p);
            if (text.Length > 0) blocks.Add(new ParagraphBlock(text) { Role = role });
        }
    }

    private List<IReadOnlyList<string>> Rows(Table table) =>
        [.. table.Elements<TableRow>().Select(r => (IReadOnlyList<string>)[.. r.Elements<TableCell>().Select(c => TextCleanup.Normalize(string.Join(' ', c.Elements<Paragraph>().Select(Text))))])];

    /// <summary>Heading levels by style ID, from the style's outline level or its "heading N" name.</summary>
    private static Dictionary<string, int> HeadingLevels(MainDocumentPart main)
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
        {
            var id = style.StyleId?.Value;
            if (id is null) continue;
            if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is int outline && outline < 9)
            {
                levels[id] = outline + 1;
            }
            else if (style.StyleName?.Val?.Value is { } name && name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[8..], out var n))
            {
                levels[id] = n;
            }
        }

        return levels;
    }

    private static Dictionary<string, string> Properties(WordprocessingDocument doc)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) properties[key] = value;
        }

        Add("author", doc.PackageProperties.Creator);
        Add("lastModifiedBy", doc.PackageProperties.LastModifiedBy);
        Add("revision", doc.PackageProperties.Revision);
        Add("subject", doc.PackageProperties.Subject);
        Add("created", doc.PackageProperties.Created?.ToString("O"));
        Add("modified", doc.PackageProperties.Modified?.ToString("O"));
        foreach (var custom in doc.CustomFilePropertiesPart?.Properties?.Elements<CustomDocumentProperty>() ?? [])
        {
            Add($"custom:{custom.Name?.Value}", custom.InnerText);
        }

        return properties;
    }

    /// <summary>Word stores list numbers as rules, not text: count paragraphs per list and level to rebuild "1.", "2.".</summary>
    private sealed class ListNumbering(MainDocumentPart main)
    {
        private readonly Numbering? _numbering = main.NumberingDefinitionsPart?.Numbering;
        private readonly Dictionary<(int, int), int> _counters = [];

        public (string Marker, int Level) Next(NumberingProperties properties)
        {
            var numId = properties.NumberingId?.Val?.Value ?? 0;
            var level = properties.NumberingLevelReference?.Val?.Value ?? 0;
            var format = Format(numId, level);
            if (format is null || format == NumberFormatValues.Bullet) return ("-", level + 1);

            var key = (numId, level);
            _counters[key] = _counters.GetValueOrDefault(key) + 1;
            return ($"{_counters[key]}.", level + 1);
        }

        private NumberFormatValues? Format(int numId, int level)
        {
            var instance = _numbering?.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var abstractId = instance?.AbstractNumId?.Val?.Value;
            var definition = _numbering?.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
            return definition?.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level)?.NumberingFormat?.Val?.Value;
        }
    }
}
