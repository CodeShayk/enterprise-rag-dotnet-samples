using OpenCvSharp;

namespace Contoso.Knowledge.Ocr;

/// <summary>
/// Stamps, signatures and pen notes: colored ink that was dropped from the page image before OCR. Each cluster
/// of colored ink is cropped from the marks-only image and read on its own, so a stamp never merges with the
/// printed line under it. Handwriting reads poorly or not at all; the confidence says so.
/// </summary>
public static class Marks
{
    public static IReadOnlyList<OcrLine> Read(PreparedPage page, IOcrEngine engine)
    {
        if (page.Marks is null) return [];
        using var grown = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(41, 21));
        Cv2.Dilate(page.ColoredInk, grown, kernel);
        Cv2.FindContours(grown, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var lines = new List<OcrLine>();
        var bounds = new Rect(0, 0, page.Image.Width, page.Image.Height);
        foreach (var rect in contours.Select(c => Cv2.BoundingRect(c)).Where(r => r.Width * r.Height >= 2_000).OrderBy(r => r.Y).ThenBy(r => r.X))
        {
            var padded = new Rect(rect.X - 10, rect.Y - 10, rect.Width + 20, rect.Height + 20).Intersect(bounds);
            using var crop = new Mat(page.Marks, padded);
            var words = engine.RecognizeRegion(crop).Lines.Select(l => Offset(l, padded.X, padded.Y)).SelectMany(l => l.Words).ToList();
            if (words.Count == 0) continue;

            // One mark, one line of text: a stamp's lines and words belong together, in reading order.
            lines.Add(TesseractEngine.Line(Chain(words)));
        }

        return lines;
    }

    /// <summary>
    /// Words in reading order on a tilted mark: each row grows from left to right by taking the next word whose
    /// middle is close to the last word's, so a row can follow the slope of a stamp pressed on at an angle.
    /// </summary>
    private static List<OcrWord> Chain(List<OcrWord> words)
    {
        var rows = new List<List<OcrWord>>();
        foreach (var word in words.OrderBy(w => w.Box.Left))
        {
            var row = rows.Where(r => r[^1].Box.Right <= word.Box.Left + 5
                    && Math.Abs(r[^1].Box.CenterY - word.Box.CenterY) < 0.6 * Math.Max(r[^1].Box.Height, word.Box.Height))
                .OrderBy(r => Math.Abs(r[^1].Box.CenterY - word.Box.CenterY)).FirstOrDefault();
            if (row is null) rows.Add([word]);
            else row.Add(word);
        }

        return [.. rows.OrderBy(r => r.Average(w => w.Box.CenterY)).SelectMany(r => r)];
    }

    private static OcrLine Offset(OcrLine line, double dx, double dy) => line with
    {
        Box = Move(line.Box, dx, dy),
        Words = [.. line.Words.Select(w => w with { Box = Move(w.Box, dx, dy) })],
    };

    private static PixelBox Move(PixelBox b, double dx, double dy) => new(b.Left + dx, b.Top + dy, b.Right + dx, b.Bottom + dy);
}
