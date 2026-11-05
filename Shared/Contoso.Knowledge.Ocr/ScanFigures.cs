using OpenCvSharp;

namespace Contoso.Knowledge.Ocr;

/// <summary>
/// Figures on a scan: ink that isn't text. The text lines and table grids are masked out, what remains is
/// thickened so the strokes of one drawing join, and each large enough region is a figure.
/// </summary>
public static class ScanFigures
{
    public static IReadOnlyList<PixelBox> Find(Mat gray, IEnumerable<PixelBox> textLines, IEnumerable<PixelBox> tables)
    {
        using var ink = new Mat();
        Cv2.Threshold(gray, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        foreach (var box in textLines.Concat(tables))
        {
            Cv2.Rectangle(ink, new Rect((int)box.Left - 4, (int)box.Top - 4, (int)box.Width + 8, (int)box.Height + 8), Scalar.All(0), -1);
        }

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(25, 25));
        using var joined = new Mat();
        Cv2.Dilate(ink, joined, kernel);
        Cv2.FindContours(joined, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return [.. contours.Select(c => Cv2.BoundingRect(c))
            .Where(r => r.Width > gray.Width / 10 && r.Height > gray.Height / 20)
            .Select(r => new PixelBox(r.Left + 12, r.Top + 12, r.Right - 12, r.Bottom - 12))
            .OrderBy(b => b.Top)];
    }
}
