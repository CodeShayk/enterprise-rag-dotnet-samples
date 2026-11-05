using OpenCvSharp;

namespace Contoso.Knowledge.Ocr;

/// <summary>
/// How good a scan is, measured on the page image before any repair: capture resolution, skew, contrast between
/// ink and paper, specks per megapixel and sharpness (the spread of the Laplacian around the ink, at 300 DPI).
/// Contrast and sharpness are measured on the ink, so a page that is mostly blank isn't penalized. The grade
/// routes pages; its thresholds are a starting point to calibrate against error rates on a reviewed sample.
/// </summary>
public sealed record ScanQuality(int Dpi, double SkewDegrees, double Contrast, double SpecksPerMegapixel, double Sharpness)
{
    public string Grade
    {
        get
        {
            var problems = (Dpi < 250 ? 1 : 0) + (Math.Abs(SkewDegrees) > 2 ? 1 : 0) + (Contrast < 0.5 ? 1 : 0)
                + (SpecksPerMegapixel > 150 ? 1 : 0) + (Sharpness < 40 ? 1 : 0);
            return problems switch { 0 => "good", 1 => "fair", _ => "poor" };
        }
    }

    public static ScanQuality Measure(Mat page, int dpi)
    {
        using var gray = new Mat();
        if (page.Channels() == 3) Cv2.CvtColor(page, gray, ColorConversionCodes.BGR2GRAY);
        else page.CopyTo(gray);
        using var at300 = new Mat();
        Cv2.Resize(gray, at300, new Size(), 300.0 / dpi, 300.0 / dpi, InterpolationFlags.Cubic);

        // Ink and paper split by Otsu's threshold; contrast is the difference of their mean levels.
        using var ink = new Mat();
        Cv2.Threshold(at300, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var paper = new Mat();
        Cv2.BitwiseNot(ink, paper);
        var contrast = (Cv2.Mean(at300, paper).Val0 - Cv2.Mean(at300, ink).Val0) / 255;

        // Sharpness: blurred strokes have soft edges, so the Laplacian varies less around them.
        using var edges = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(ink, edges, kernel);
        using var laplacian = new Mat();
        Cv2.Laplacian(at300, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var deviation, edges);

        using var copy = at300.Clone();
        var specks = ImagePrep.Despeckle(copy);
        return new ScanQuality(dpi, ImagePrep.EstimateSkew(at300), Math.Round(contrast, 2),
            Math.Round(specks / (at300.Width * (double)at300.Height / 1e6), 1), Math.Round(deviation.Val0));
    }
}
