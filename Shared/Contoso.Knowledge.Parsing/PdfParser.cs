using Contoso.Knowledge.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Contoso.Knowledge.Parsing;

public enum PdfExtraction
{
    /// <summary>Each page's text in content-stream order, as a quick integration would take it.</summary>
    Naive,

    /// <summary>Words, blocks, reading order, running headers and footers, tables and figures from the page geometry.</summary>
    Layout,
}

public sealed record PdfParserOptions
{
    public PdfExtraction Mode { get; init; } = PdfExtraction.Layout;
}

/// <summary>
/// Born-digital PDFs through PdfPig. A PDF stores glyphs at positions, not paragraphs, so the layout mode
/// rebuilds structure from geometry: words from letters, blocks from words, columns and reading order from block
/// positions, tables from ruling lines, figures from framed regions. Pages without a text layer are reported as
/// warnings for OCR (Chapter 8).
/// </summary>
public sealed class PdfParser(PdfParserOptions? options = null) : IDocumentParser
{
    private readonly PdfParserOptions _options = options ?? new PdfParserOptions();

    public string Name => "pdf";

    public string Version => _options.Mode == PdfExtraction.Layout ? "1.0" : "1.0-naive";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Pdf];

    public Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        using var copy = new MemoryStream();
        content.CopyTo(copy);
        using var pdf = PdfDocument.Open(copy.ToArray());
        if (pdf.NumberOfPages > context.Limits.MaxPages)
        {
            throw new ParserLimitException($"{context.FileName} has {pdf.NumberOfPages} pages; the limit is {context.Limits.MaxPages}.");
        }

        var pages = new List<PageInfo>();
        var warnings = new List<string>();
        var perPage = new List<List<Block>>();
        var allPages = pdf.GetPages().ToList();
        var bodySize = BodySize(allPages);
        var running = RunningText.Find(allPages);
        foreach (var page in allPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasText = page.Letters.Count > 0;
            pages.Add(new PageInfo(page.Number, page.Width, page.Height) { HasTextLayer = hasText });
            if (!hasText)
            {
                warnings.Add($"Page {page.Number} has no text layer{(page.GetImages().Any() ? " but contains an image" : "")}: it needs OCR (Chapter 8).");
                perPage.Add([]);
                continue;
            }

            perPage.Add(_options.Mode == PdfExtraction.Naive
                ? [new ParagraphBlock(page.Text) { Anchor = new SourceAnchor { Page = page.Number } }]
                : PageLayout(page, bodySize, running));
        }

        var blocks = _options.Mode == PdfExtraction.Naive ? perPage.SelectMany(b => b).ToList() : Continuations.Merge(perPage);
        var properties = new Dictionary<string, string>();
        if (pdf.Information.Author is { } author) properties["author"] = author;
        if (pdf.Information.Producer is { } producer) properties["producer"] = producer;
        if (pdf.Information.CreationDate is { } created) properties["created"] = created;
        return Task.FromResult(new ParsedContent(blocks)
        {
            Pages = pages,
            Title = pdf.Information.Title,
            Properties = properties,
            Warnings = warnings,
        });
    }

    /// <summary>The most common font size on the document's pages: the body text, against which headings stand out.</summary>
    private static double BodySize(IEnumerable<Page> pages) =>
        pages.SelectMany(p => p.Letters).GroupBy(l => Math.Round(l.PointSize * 2) / 2).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(10);

    private static List<Block> PageLayout(Page page, double bodySize, RunningText running)
    {
        var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var regions = new List<(PdfRectangle Box, Block Block)>();

        // Running headers and footers first: kept with their role, so that projections can leave them out.
        foreach (var (box, text, role) in running.On(page, words))
        {
            regions.Add((box, new ParagraphBlock(text) { Role = role }));
        }

        words = [.. words.Where(w => !running.Covers(page, w))];

        foreach (var table in RuledTables.Find(page, words, bodySize))
        {
            regions.Add((table.Box, table.Block));
            words = [.. words.Except(table.Words)];
        }

        foreach (var figure in Figures.Find(page, words))
        {
            regions.Add((figure.Box, figure.Block));
            words = [.. words.Except(figure.Words)];
        }

        // List markers sit apart from their text, so the segmenter can put them in blocks of their own.
        // Take them out first and give each back to the line on its baseline.
        var markers = ListMarkers(words);
        words = [.. words.Except(markers.Select(m => m.Word))];

        foreach (var block in DocstrumBoundingBoxes.Instance.GetBlocks(words))
        {
            foreach (var (box, classified) in Classify(block, bodySize, markers))
            {
                regions.Add((box, classified));
            }
        }

        return [.. ReadingOrder.Sort(regions, page.Width).Select(r => r.Block with { Anchor = Anchor(page, r.Box) })];
    }

    /// <summary>Bullets and "1." markers followed, on the same baseline, by a word within 30 points.</summary>
    private static List<(Word Word, double Baseline)> ListMarkers(IReadOnlyList<Word> words) =>
        [.. words.Where(w => w.Text == "•" || (w.Text.Length <= 3 && w.Text.EndsWith('.') && w.Text[..^1].All(char.IsDigit)))
            .Where(m => words.Any(o => o != m && Math.Abs(Baseline(o) - Baseline(m)) < 1.5 && o.BoundingBox.Left - m.BoundingBox.Right is > 0 and < 30))
            .Where(m => !words.Any(o => o != m && Math.Abs(Baseline(o) - Baseline(m)) < 1.5 && m.BoundingBox.Left - o.BoundingBox.Right is > -1 and < 12))
            .Select(m => (m, Baseline(m)))];

    private static double Baseline(Word word) => word.Letters[0].StartBaseLine.Y;

    /// <summary>Splits a segmenter block where the style changes or a list item starts, and labels each part.</summary>
    private static IEnumerable<(PdfRectangle Box, Block Block)> Classify(TextBlock block, double bodySize, List<(Word Word, double Baseline)> markers)
    {
        var groups = new List<(List<TextLine> Lines, Word? Marker)>();
        foreach (var line in block.TextLines)
        {
            var style = Style(line);
            var first = line.Words.OrderBy(w => w.BoundingBox.Left).First();
            var marker = markers.FirstOrDefault(m => Math.Abs(m.Baseline - Baseline(first)) < 1.5 && first.BoundingBox.Left - m.Word.BoundingBox.Right is > 0 and < 30).Word;
            var previous = groups.Count > 0 ? groups[^1].Lines[^1] : null;
            if (previous is null || Style(previous) != style || marker is not null || ListMarker(line.Text) is not null)
            {
                groups.Add(([line], marker));
            }
            else
            {
                groups[^1].Lines.Add(line);
            }
        }

        foreach (var (group, markerWord) in groups)
        {
            var box = Union(group.Select(l => l.BoundingBox).Concat(markerWord is null ? [] : [markerWord.BoundingBox]));
            var text = TextCleanup.Normalize(TextCleanup.JoinLines(group.Select(l => l.Text)));
            var (bold, italic, size) = Style(group[0]);
            if (markerWord is not null)
            {
                yield return (box, new ListItemBlock(text, 1, markerWord.Text == "•" ? "-" : markerWord.Text));
            }
            else if (bold && size >= bodySize - 0.6 && text.Length < 120)
            {
                yield return (box, new HeadingBlock(size >= 14 ? 1 : size >= 12 ? 2 : 3, text));
            }
            else if (ListMarker(text) is { } marker)
            {
                yield return (box, new ListItemBlock(TextCleanup.Normalize(text[marker.Length..]), 1, marker == "•" ? "-" : marker));
            }
            else if (italic && text.StartsWith("Figure", StringComparison.Ordinal))
            {
                yield return (box, new FigureBlock(text));
            }
            else
            {
                yield return (box, new ParagraphBlock(text));
            }
        }
    }

    private static (bool Bold, bool Italic, double Size) Style(TextLine line)
    {
        var letter = line.Words.SelectMany(w => w.Letters).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Value));
        var font = letter?.FontName ?? "";
        return (font.Contains("Bold", StringComparison.OrdinalIgnoreCase),
            font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase),
            Math.Round((letter?.PointSize ?? 0) * 2) / 2);
    }

    /// <summary>A bullet or "1." at the start of a line, the markers lists use.</summary>
    internal static string? ListMarker(string text)
    {
        if (text.StartsWith('•')) return "•";
        var dot = text.IndexOf('.');
        return dot is > 0 and <= 3 && text[..dot].All(char.IsDigit) && text.Length > dot + 1 && (text[dot + 1] == ' ' || char.IsUpper(text[dot + 1]))
            ? text[..(dot + 1)]
            : null;
    }

    internal static PdfRectangle Union(IEnumerable<PdfRectangle> boxes)
    {
        var list = boxes.ToList();
        return new PdfRectangle(list.Min(b => b.Left), list.Min(b => b.Bottom), list.Max(b => b.Right), list.Max(b => b.Top));
    }

    /// <summary>Converts PDF coordinates (origin bottom left) to the model's (origin top left).</summary>
    private static SourceAnchor Anchor(Page page, PdfRectangle box) => new()
    {
        Page = page.Number,
        Box = new BoundingBox(Math.Round(box.Left, 1), Math.Round(page.Height - box.Top, 1), Math.Round(box.Right, 1), Math.Round(page.Height - box.Bottom, 1)),
    };
}
