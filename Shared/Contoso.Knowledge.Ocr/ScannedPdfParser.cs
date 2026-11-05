using System.Text.RegularExpressions;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Parsing;
using UglyToad.PdfPig;

namespace Contoso.Knowledge.Ocr;

/// <summary>
/// PDFs with pages that have no text layer. Triage decides page by page: pages with a usable text layer go to
/// Chapter 7's layout-aware parser, scanned pages are rendered, prepared, read and laid out here, and the two
/// are merged in page order, so a mixed document (a contract with a signed, scanned last page) comes out as one.
/// One engine reads one page at a time; register one parser per worker to read in parallel.
/// </summary>
public sealed partial class ScannedPdfParser(IOcrEngine engine, PrepOptions? prep = null, double reviewBelow = 0.8, bool postCorrect = true) : IDocumentParser
{
    private readonly PdfParser _digital = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Name => "pdf";

    /// <summary>The engine is part of the version: changing it changes the text, and so what must be reprocessed.</summary>
    public string Version => $"1.0+{engine.Name}{(postCorrect ? "" : "-raw")}";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Pdf];

    /// <summary>The review items of the last document parsed, for the quality-control queue.</summary>
    public IReadOnlyList<ReviewItem> LastReview { get; private set; } = [];

    public async Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var pdf = buffer.ToArray();
        var triage = Triage.Assess(pdf);
        var digital = await _digital.ParseAsync(new MemoryStream(pdf), context, cancellationToken);
        var scanned = triage.Where(t => t.Kind == PageKind.Scanned).ToList();
        if (scanned.Count == 0) return digital;

        var pages = new List<ScanPage>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var t in scanned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dpi = t.NativeDpi ?? 300;
                using var image = PageImages.Render(pdf, t.Page, dpi);
                using var prepared = ImagePrep.Prepare(image, dpi, prep ?? PrepOptions.Default);
                pages.Add(ScanLayout.Read(t.Page, prepared, engine, reviewBelow, postCorrect));
            }
        }
        finally
        {
            _gate.Release();
        }

        // Digital and scanned blocks in page order, then the continuations that page breaks split (Chapter 7).
        var byPage = digital.Blocks.Where(b => b.Anchor?.Page is not null).GroupBy(b => b.Anchor!.Page!.Value)
            .Select(g => (Page: g.Key, Blocks: (IEnumerable<Block>)g))
            .Concat(pages.Select(p => (Page: p.Number, Blocks: (IEnumerable<Block>)p.Blocks)))
            .OrderBy(p => p.Page).Select(p => p.Blocks);
        var blocks = Continuations.Merge(byPage);
        LastReview = [.. pages.SelectMany(p => p.Review)];

        var properties = new Dictionary<string, string>(digital.Properties)
        {
            ["ocr"] = $"{engine.Name}; pages {string.Join(", ", scanned.Select(s => s.Page))}",
            ["ocr-skew"] = string.Join("; ", pages.Select(p => $"page {p.Number}: {p.SkewDegrees:F2}°")),
            ["ocr-corrections"] = $"{pages.Sum(p => p.Corrections)} post-corrections, {pages.Sum(p => p.CellsReread)} table cells read again",
        };
        if (References(blocks) is { Length: > 0 } references) properties["references"] = references;

        return digital with
        {
            Blocks = blocks,
            Properties = properties,
            Warnings = [.. digital.Warnings.Where(w => !w.Contains("text layer", StringComparison.OrdinalIgnoreCase)),
                        .. LastReview.Count == 0 ? [] : new[] { $"{LastReview.Count} OCR items below confidence {reviewBelow:F2} or handwritten, queued for review" }],
        };
    }

    /// <summary>
    /// Links body references ("Table 6-2 gives...", "see Figure 8-1") to the blocks they name: a captioned table or
    /// figure by its number, or an uncaptioned table by the paragraph just before it that names a table.
    /// </summary>
    private static string References(IReadOnlyList<Block> blocks)
    {
        var targets = new Dictionary<string, Block>(StringComparer.Ordinal);
        for (var i = 0; i < blocks.Count; i++)
        {
            var label = blocks[i] switch
            {
                TableBlock { Caption: { } c } => Label().Match(c).Value,
                FigureBlock { Caption: { } c } => Label().Match(c).Value,
                TableBlock when i > 0 && blocks[i - 1] is ParagraphBlock p && Label().Match(p.Text) is { Success: true } m && m.Value.StartsWith("Table", StringComparison.Ordinal) => m.Value,
                _ => "",
            };
            if (label.Length > 0) targets.TryAdd(label, blocks[i]);
        }

        var mentioned = blocks.OfType<ParagraphBlock>().Where(p => p.Role is null).SelectMany(p => Label().Matches(p.Text).Select(m => m.Value)).ToHashSet();
        return string.Join("; ", targets.Where(t => mentioned.Contains(t.Key))
            .Select(t => $"{t.Key} -> page {t.Value.Anchor?.Page}, box {Box(t.Value.Anchor?.Box)}"));
    }

    private static string Box(BoundingBox? b) => b is { } x ? $"{x.Left:F0},{x.Top:F0},{x.Right:F0},{x.Bottom:F0} pt" : "-";

    [GeneratedRegex(@"\b(Table|Figure) \d+(-\d+)?")]
    private static partial Regex Label();
}

/// <summary>What triage decided about one PDF page.</summary>
public enum PageKind
{
    /// <summary>A usable text layer: parse it, don't OCR it.</summary>
    TextLayer,

    /// <summary>A page-sized image and no text: OCR it.</summary>
    Scanned,

    /// <summary>A page-sized image under a text layer someone else's OCR produced: its quality is unknown.</summary>
    ScannedWithText,

    /// <summary>Neither text nor an image.</summary>
    Blank,
}

public sealed record PageTriage(int Page, PageKind Kind, int Characters, double Readable, double ImageCoverage, int? NativeDpi);

/// <summary>
/// Triage: which pages need OCR. Characters say whether there is a text layer, the share of readable characters
/// whether it's usable (a font without a Unicode map yields private-use or replacement characters), and the
/// share of the page covered by images whether the page is a scan.
/// </summary>
public static class Triage
{
    public static IReadOnlyList<PageTriage> Assess(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return [.. document.GetPages().Select(page =>
        {
            var area = page.Width * page.Height;
            var images = page.GetImages().ToList();
            var coverage = Math.Min(1, images.Sum(i => i.BoundingBox.Width * i.BoundingBox.Height) / area);
            var text = string.Concat(page.Letters.Select(l => l.Value)).Where(c => !char.IsWhiteSpace(c)).ToList();
            var readable = text.Count == 0 ? 0 : (double)text.Count(Readable) / text.Count;
            var largest = images.OrderByDescending(i => i.BoundingBox.Width * i.BoundingBox.Height).FirstOrDefault();
            int? dpi = largest is null ? null : (int)Math.Round(largest.WidthInSamples / (largest.BoundingBox.Width / 72));
            var kind = (text.Count >= 20 && readable >= 0.9, coverage >= 0.8) switch
            {
                (true, true) => PageKind.ScannedWithText,
                (true, false) => PageKind.TextLayer,
                (false, true) => PageKind.Scanned,
                _ => text.Count > 0 ? PageKind.Scanned : PageKind.Blank,
            };
            return new PageTriage(page.Number, kind, text.Count, Math.Round(readable, 3), Math.Round(coverage, 2), dpi);
        })];
    }

    /// <summary>Letters, digits, punctuation and symbols; not private-use, replacement or control characters.</summary>
    public static bool Readable(char c) => !char.IsControl(c) && c != '�' && !(c >= '' && c <= '');
}
