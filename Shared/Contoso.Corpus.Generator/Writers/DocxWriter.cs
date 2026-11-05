using Contoso.Knowledge.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.VariantTypes;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Contoso.Corpus.Generator.Writers;

/// <summary>A tracked change left in the file: <see cref="Deleted"/> replaced by <see cref="Inserted"/> in block <see cref="Block"/>.</summary>
internal sealed record TrackedChange(int Block, string Deleted, string Inserted, string Author, DateTime Date);

/// <summary>A reviewer's comment on block <see cref="Block"/>.</summary>
internal sealed record ReviewComment(int Block, string Author, string Text, DateTime Date);

internal sealed record DocxOptions
{
    public string? Header { get; init; }

    public string? Footer { get; init; }

    public IReadOnlyList<TrackedChange> Changes { get; init; } = [];

    public IReadOnlyList<ReviewComment> Comments { get; init; } = [];

    /// <summary>Custom document properties, such as an effective date, that a metadata extractor can read.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Writes a canonical document as Word: heading styles, real Word numbering (so list numbers aren't in the text),
/// tables with a repeated header row, tracked changes, comments, a running header and footer, and properties.
/// </summary>
internal static class DocxWriter
{
    public static byte[] Write(CanonicalDocument truth, DocxOptions options, string author, DateTime created)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            doc.PackageProperties.Title = truth.Metadata.Title;
            doc.PackageProperties.Creator = author;
            doc.PackageProperties.LastModifiedBy = author;
            doc.PackageProperties.Created = created;
            doc.PackageProperties.Modified = created;
            doc.PackageProperties.Revision = truth.Metadata.Revision ?? "1";
            doc.PackageProperties.Subject = truth.Metadata.DocumentType;

            var main = doc.AddMainDocumentPart();
            AddStyles(main);
            AddNumbering(main, out var bulletNum, out var nextNum);
            var body = new Body();
            main.Document = new Document(body);

            var changes = options.Changes.ToLookup(c => c.Block);
            var comments = options.Comments.ToLookup(c => c.Block);
            var commentId = 0;
            var commentsPart = options.Comments.Count > 0 ? main.AddNewPart<WordprocessingCommentsPart>() : null;
            if (commentsPart is not null) commentsPart.Comments = new Comments();

            int? currentList = null;
            for (var i = 0; i < truth.Blocks.Count; i++)
            {
                var block = truth.Blocks[i];
                if (block.Role == BlockRoles.Comment) continue;   // comments are written as Word comments below
                if (block is not ListItemBlock) currentList = null;

                OpenXmlElement element = block switch
                {
                    HeadingBlock h => StyledParagraph($"Heading{Math.Clamp(h.Level, 1, 3)}", h.Text, changes[i]),
                    ParagraphBlock p => StyledParagraph(null, p.Text, changes[i]),
                    ListItemBlock item => ListParagraph(item, changes[i], bulletNum, ref currentList, nextNum),
                    TableBlock t => Table(t),
                    FigureBlock f => StyledParagraph("Caption", f.Caption ?? "", []),
                    CodeBlock c => StyledParagraph("Code", c.Text, []),
                    _ => throw new NotSupportedException(block.GetType().Name),
                };

                if (block is TableBlock { Caption: { } caption })
                {
                    body.Append(StyledParagraph("Caption", caption, []));
                }

                foreach (var comment in comments[i])
                {
                    var id = (commentId++).ToString();
                    commentsPart!.Comments!.Append(new Comment(new Paragraph(new Run(new Text(comment.Text))))
                    {
                        Id = id, Author = comment.Author, Initials = Initials(comment.Author), Date = comment.Date,
                    });
                    if (element is Paragraph paragraph)
                    {
                        paragraph.InsertAt(new CommentRangeStart { Id = id }, paragraph.Elements<ParagraphProperties>().Any() ? 1 : 0);
                        paragraph.Append(new CommentRangeEnd { Id = id }, new Run(new CommentReference { Id = id }));
                    }
                }

                body.Append(element);
            }

            body.Append(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1440, Bottom = 1440, Left = 1300, Right = 1300, Header = 700, Footer = 700 }));
            AddHeaderFooter(main, body, options);
            AddCustomProperties(doc, options.Properties);
            main.Document.Save();
        }

        return Zip.Normalize(stream.ToArray(), created);
    }

    private static Paragraph StyledParagraph(string? style, string text, IEnumerable<TrackedChange> changes)
    {
        var paragraph = new Paragraph();
        if (style is not null)
        {
            paragraph.Append(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        }

        AppendRuns(paragraph, text, changes.ToList());
        return paragraph;
    }

    /// <summary>Writes the text, turning each tracked change into a deletion followed by an insertion.</summary>
    private static void AppendRuns(Paragraph paragraph, string text, List<TrackedChange> changes)
    {
        var position = 0;
        var revision = 1;
        foreach (var change in changes.OrderBy(c => text.IndexOf(c.Inserted, StringComparison.Ordinal)))
        {
            var at = text.IndexOf(change.Inserted, position, StringComparison.Ordinal);
            if (at < 0) throw new InvalidOperationException($"Tracked change \"{change.Inserted}\" isn't in \"{text}\".");
            if (at > position) paragraph.Append(TextRun(text[position..at]));
            if (change.Deleted.Length > 0)
            {
                paragraph.Append(new DeletedRun(new Run(new DeletedText(change.Deleted) { Space = SpaceProcessingModeValues.Preserve }))
                {
                    Id = $"{revision++}", Author = change.Author, Date = change.Date,
                });
            }

            paragraph.Append(new InsertedRun(TextRun(change.Inserted)) { Id = $"{revision++}", Author = change.Author, Date = change.Date });
            position = at + change.Inserted.Length;
        }

        if (position < text.Length) paragraph.Append(TextRun(text[position..]));
    }

    private static Run TextRun(string text) => new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static Paragraph ListParagraph(ListItemBlock item, IEnumerable<TrackedChange> changes, int bulletNum, ref int? currentList, Func<int> nextNum)
    {
        var numbered = item.Marker is { } m && char.IsDigit(m[0]);
        if (numbered && currentList is null)
        {
            currentList = nextNum();   // each numbered list restarts at 1
        }

        var paragraph = new Paragraph(new ParagraphProperties(
            new ParagraphStyleId { Val = "ListParagraph" },
            new NumberingProperties(new NumberingLevelReference { Val = item.Level - 1 }, new NumberingId { Val = numbered ? currentList!.Value : bulletNum })));
        AppendRuns(paragraph, item.Text, changes.ToList());
        return paragraph;
    }

    private static Table Table(TableBlock table)
    {
        var columns = table.Rows.Max(r => r.Count);
        var result = new Table(new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));
        result.Append(new TableGrid(Enumerable.Range(0, columns).Select(_ => new GridColumn { Width = $"{9000 / columns}" })));
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = new TableRow();
            if (r < table.HeaderRows)
            {
                row.Append(new TableRowProperties(new TableHeader()));
            }

            for (var c = 0; c < columns; c++)
            {
                var text = c < table.Rows[r].Count ? table.Rows[r][c] : "";
                var run = r < table.HeaderRows ? new Run(new RunProperties(new Bold()), new Text(text)) : new Run(new Text(text));
                row.Append(new TableCell(new Paragraph(run)));
            }

            result.Append(row);
        }

        return result;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = new Styles(
            new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "22" }))),
            new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            Heading(1, "32"), Heading(2, "26"), Heading(3, "24"),
            new Style(new StyleName { Val = "List Paragraph" }, new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(new Indentation { Left = "720" })) { Type = StyleValues.Paragraph, StyleId = "ListParagraph" },
            new Style(new StyleName { Val = "caption" }, new BasedOn { Val = "Normal" },
                new StyleRunProperties(new Italic())) { Type = StyleValues.Paragraph, StyleId = "Caption" },
            new Style(new StyleName { Val = "Code" }, new BasedOn { Val = "Normal" },
                new StyleRunProperties(new RunFonts { Ascii = "Consolas" })) { Type = StyleValues.Paragraph, StyleId = "Code" },
            new Style(new StyleName { Val = "Table Grid" }) { Type = StyleValues.Table, StyleId = "TableGrid" });
        main.AddNewPart<StyleDefinitionsPart>().Styles = styles;

        static Style Heading(int level, string size) =>
            new(new StyleName { Val = $"heading {level}" }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" },
                new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "80" }, new OutlineLevel { Val = level - 1 }),
                new StyleRunProperties(new Bold(), new FontSize { Val = size }))
            { Type = StyleValues.Paragraph, StyleId = $"Heading{level}" };
    }

    private static void AddNumbering(MainDocumentPart main, out int bulletNum, out Func<int> nextNum)
    {
        var numbering = new Numbering(
            new AbstractNum(
                new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal },
                    new LevelText { Val = "%1." }, new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(new Indentation { Left = "720", Hanging = "360" })) { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new AbstractNum(
                new Level(new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "•" },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(new Indentation { Left = "720", Hanging = "360" })) { LevelIndex = 0 })
            { AbstractNumberId = 2 },
            new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 1 });
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = numbering;
        bulletNum = 1;
        var next = 1;
        nextNum = () =>
        {
            next++;
            numbering.Append(new NumberingInstance(new AbstractNumId { Val = 1 },
                new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = 0 }) { NumberID = next });
            return next;
        };
    }

    private static void AddHeaderFooter(MainDocumentPart main, Body body, DocxOptions options)
    {
        var section = body.Elements<SectionProperties>().Last();
        if (options.Header is not null)
        {
            var part = main.AddNewPart<HeaderPart>();
            part.Header = new Header(new Paragraph(new Run(new Text(options.Header))));
            section.PrependChild(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(part) });
        }

        if (options.Footer is not null)
        {
            var part = main.AddNewPart<FooterPart>();
            part.Footer = new Footer(new Paragraph(new Run(new Text(options.Footer))));
            section.PrependChild(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(part) });
        }
    }

    private static void AddCustomProperties(WordprocessingDocument doc, IReadOnlyDictionary<string, string> properties)
    {
        if (properties.Count == 0) return;
        var part = doc.AddCustomFilePropertiesPart();
        part.Properties = new Properties();
        var id = 2;
        foreach (var (name, value) in properties)
        {
            part.Properties.Append(new CustomDocumentProperty(new VTLPWSTR(value))
            {
                FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}", PropertyId = id++, Name = name,
            });
        }
    }

    private static string Initials(string name) => string.Concat(name.Split(' ').Select(p => p[0]));
}
