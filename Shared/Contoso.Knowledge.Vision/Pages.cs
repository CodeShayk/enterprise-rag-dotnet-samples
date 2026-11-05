using System.Globalization;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Ocr;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Contoso.Knowledge.Vision;

/// <summary>A word on a page and its box, in points from the page's top left corner, as anchors are (Chapter 6).</summary>
public sealed record PageWord(string Text, BoundingBox Box);

/// <summary>The words of one page with their boxes: from the text layer on a born-digital page, from OCR on a scan.</summary>
public static class PageWords
{
    public static IReadOnlyList<PageWord> Digital(byte[] pdf, int pageNumber)
    {
        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(pageNumber);
        return [.. page.GetWords().Select(w => new PageWord(w.Text,
            new BoundingBox(w.BoundingBox.Left, page.Height - w.BoundingBox.Top, w.BoundingBox.Right, page.Height - w.BoundingBox.Bottom)))];
    }

    /// <summary>OCR's words mapped back to the page's own frame, the crooked scan a viewer shows (Chapter 8).</summary>
    public static IReadOnlyList<PageWord> Scanned(byte[] pdf, int pageNumber, IOcrEngine engine, int dpi = 300)
    {
        using var image = PageImages.Render(pdf, pageNumber, dpi);
        using var prepared = ImagePrep.Prepare(image, dpi, PrepOptions.Default);
        return [.. engine.Recognize(prepared.Image).Lines.SelectMany(l => l.Words).Select(w => new PageWord(w.Text, prepared.ToPagePoints(w.Box)))];
    }

    /// <summary>
    /// The box of a quote on the page. Words are matched in reading order, comparing letters and digits only (so
    /// "85 N·m" finds "85 N-m" where OCR read the dot as a hyphen), and a match may run from the end of a line to a line
    /// just below it in the same column. A table row quoted with its column names isn't on the page as written, so
    /// when the whole quote isn't found, its cells are, and the row's box is the union of those on the same row.
    /// </summary>
    public static BoundingBox? Locate(IReadOnlyList<PageWord> words, string quote)
    {
        var lines = Lines(words);
        if (Match(lines, Letters(quote)) is { } whole) return whole;
        var cells = quote.Split(['|', ';', ':', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(Letters).Where(c => c.Length >= 2).Distinct()
            .Select(c => Match(lines, c)).OfType<BoundingBox>().ToList();
        if (cells.Count == 0) return null;
        var anchor = cells.OrderByDescending(b => b.Width * b.Height).First();
        var row = cells.Where(b => Math.Abs((b.Top + b.Bottom) / 2 - (anchor.Top + anchor.Bottom) / 2) < Math.Max(anchor.Height, b.Height)).ToList();
        return Union(row);
    }

    /// <summary>
    /// Words grouped into lines as a reader sees them. Taken left to right, a word joins the line whose last word ends
    /// just before it at the same height, so a slightly skewed scan still reads as lines, while a gap wider than a
    /// letter and a half (a column gutter or a table's column) starts a new one. Lines come top to bottom.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PageWord>> Lines(IReadOnlyList<PageWord> words)
    {
        var lines = new List<List<PageWord>>();
        foreach (var w in words.OrderBy(w => w.Box.Left))
        {
            var middle = (w.Box.Top + w.Box.Bottom) / 2;
            var line = lines.Where(l => Follows(l[^1], w)).MinBy(l => Math.Abs((l[^1].Box.Top + l[^1].Box.Bottom) / 2 - middle));
            if (line is null) lines.Add([w]);
            else line.Add(w);
        }

        return [.. lines.OrderBy(l => l[0].Box.Top).ThenBy(l => l[0].Box.Left)];
    }

    /// <summary>Whether a word continues a line that ends in <paramref name="last"/>: at its height, after it, across a short gap.</summary>
    private static bool Follows(PageWord last, PageWord word)
    {
        var height = Math.Max(last.Box.Height, word.Box.Height);
        return Math.Abs((last.Box.Top + last.Box.Bottom - word.Box.Top - word.Box.Bottom) / 2) < 0.5 * height
            && word.Box.Left > last.Box.Right - 0.2 * height && word.Box.Left - last.Box.Right < 1.5 * height;
    }

    private static BoundingBox? Match(IReadOnlyList<IReadOnlyList<PageWord>> lines, string target)
    {
        if (target.Length == 0) return null;
        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = 0; j < lines[i].Count; j++)
            {
                var run = new List<PageWord>();
                if (Continue(lines, i, j, target, 0, run, depth: 0)) return Union([.. run.Select(w => w.Box)]);
            }
        }

        return null;
    }

    /// <summary>Matches the target from position <paramref name="at"/> starting at word j of line i, then on a following line.</summary>
    private static bool Continue(IReadOnlyList<IReadOnlyList<PageWord>> lines, int i, int j, string target, int at, List<PageWord> run, int depth)
    {
        var (mark, k) = (run.Count, j);
        for (; k < lines[i].Count; k++)
        {
            var letters = Letters(lines[i][k].Text);
            if (letters.Length == 0) continue;
            var take = Math.Min(letters.Length, target.Length - at);
            if (string.CompareOrdinal(letters, 0, target, at, take) != 0 || (take < letters.Length && at + take < target.Length)) break;
            run.Add(lines[i][k]);
            at += take;
            if (at == target.Length) return true;
        }

        // The line ended and the quote goes on: try the lines just below that overlap this one horizontally (the same column).
        if (k == lines[i].Count && run.Count > mark && depth < 8)
        {
            var line = lines[i];
            var (left, right, bottom, height) = (line.Min(w => w.Box.Left), line.Max(w => w.Box.Right), line.Max(w => w.Box.Bottom), line.Max(w => w.Box.Height));
            for (var n = i + 1; n < lines.Count && lines[n][0].Box.Top < bottom + 2.5 * height; n++)
            {
                var next = lines[n];
                if (next.Min(w => w.Box.Left) < right && left < next.Max(w => w.Box.Right) && Continue(lines, n, 0, target, at, run, depth + 1)) return true;
            }
        }

        run.RemoveRange(mark, run.Count - mark);
        return false;
    }

    private static BoundingBox Union(IReadOnlyList<BoundingBox> boxes) =>
        new(boxes.Min(b => b.Left), boxes.Min(b => b.Top), boxes.Max(b => b.Right), boxes.Max(b => b.Bottom));

    private static string Letters(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}

/// <summary>
/// A citation a person can see: the document, the page and the region, as a deep link into the document viewer
/// (<c>/documents/{id}/pages/{page}?box=left,top,width,height</c>, in points) and as a highlighted page render.
/// </summary>
public sealed record VisualCitation(string DocumentId, int Page, BoundingBox Box)
{
    public string Link => string.Create(CultureInfo.InvariantCulture,
        $"/documents/{Uri.EscapeDataString(DocumentId)}/pages/{Page}?box={Box.Left:0},{Box.Top:0},{Box.Width:0},{Box.Height:0}");

    public static VisualCitation? Parse(string documentId, int page, string? box)
    {
        var parts = (box ?? "").Split(',').Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        return parts.Length == 4 && parts.All(double.IsFinite) && parts[2] > 0 && parts[3] > 0
            ? new VisualCitation(documentId, page, new BoundingBox(parts[0], parts[1], parts[0] + parts[2], parts[1] + parts[3]))
            : null;
    }
}

/// <summary>Page renders, plain or with regions highlighted, for the viewer and for citations in answers.</summary>
public static class PageRenders
{
    /// <summary>A page (numbered from 1) as a bitmap; the caller disposes it.</summary>
    public static SKBitmap Render(byte[] pdf, int pageNumber, int dpi)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            throw new PlatformNotSupportedException("PDFium rendering needs Windows, Linux or macOS.");
        }

#pragma warning disable CA1416 // the guard above covers PDFium's platforms
        return Conversion.ToImage(pdf, pageNumber - 1, options: new RenderOptions(Dpi: dpi));
#pragma warning restore CA1416
    }

    public static int PageCount(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.NumberOfPages;
    }

    /// <summary>A page's width and height in points, the frame its boxes are in.</summary>
    public static (double Width, double Height) PageSize(byte[] pdf, int pageNumber)
    {
        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(pageNumber);
        return (page.Width, page.Height);
    }

    /// <summary>The page with each box (in points) washed in yellow and outlined in orange, as a PNG.</summary>
    public static byte[] Highlight(byte[] pdf, int pageNumber, IEnumerable<BoundingBox> boxes, int dpi = 110, float padding = 3)
    {
        using var page = Render(pdf, pageNumber, dpi);
        using var canvas = new SKCanvas(page);
        using var fill = new SKPaint { Color = new SKColor(0xFF, 0xD7, 0x00, 0x60), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(0xFF, 0x8C, 0x00), Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        var scale = dpi / 72f;
        foreach (var b in boxes)
        {
            var rect = new SKRect((float)b.Left - padding, (float)b.Top - padding, (float)b.Right + padding, (float)b.Bottom + padding);
            rect = new SKRect(rect.Left * scale, rect.Top * scale, rect.Right * scale, rect.Bottom * scale);
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, stroke);
        }

        using var image = SKImage.FromBitmap(page);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
