using Contoso.Knowledge.Documents;
using OpenCvSharp;

namespace Contoso.Knowledge.Ocr;

/// <summary>What to do to a page image before OCR. Each step is measured in Chapter 8; none is free.</summary>
public sealed record PrepOptions
{
    /// <summary>The page as rendered at its native resolution.</summary>
    public static PrepOptions None { get; } = new() { TargetDpi = 0, Deskew = false, Despeckle = false, DropColoredInk = false };

    public static PrepOptions Default { get; } = new();

    /// <summary>
    /// Resample to this resolution first; 0 keeps the scan's own. Both engines are tuned for text 20 to 30 pixels
    /// high, which is about 300 DPI for body text.
    /// </summary>
    public int TargetDpi { get; init; } = 300;

    public bool Deskew { get; init; } = true;

    /// <summary>Remove isolated specks; ink that touches text, such as the dot in "N·m", stays.</summary>
    public bool Despeckle { get; init; } = true;

    /// <summary>
    /// Drop colored ink (stamps, pen) from the image the engines read, as forms scanners drop their ink color, so
    /// that a stamp over printed text doesn't destroy the line. The marks are kept in <see cref="PreparedPage.Marks"/>.
    /// </summary>
    public bool DropColoredInk { get; init; } = true;

    /// <summary>Adaptive thresholding to black and white. Engines binarize internally, so this can hurt.</summary>
    public bool Binarize { get; init; }
}

/// <summary>
/// A prepared page image and the transform that produced it, so that a box found on the prepared image can be
/// mapped back to the page as a viewer shows it. Skew is the counterclockwise correction applied, in degrees.
/// </summary>
public sealed class PreparedPage(Mat image, Mat coloredInk, Mat? marks, int dpi, int sourceDpi, double skewDegrees, double[] toSource) : IDisposable
{
    /// <summary>The grayscale image the engines read.</summary>
    public Mat Image { get; } = image;

    /// <summary>A mask of ink that isn't black or gray: stamps, signatures, handwriting in blue or red pen.</summary>
    public Mat ColoredInk { get; } = coloredInk;

    /// <summary>Only the colored ink, dark on white, when it was dropped from <see cref="Image"/>; otherwise null.</summary>
    public Mat? Marks { get; } = marks;

    public int Dpi { get; } = dpi;

    public int SourceDpi { get; } = sourceDpi;

    public double SkewDegrees { get; } = skewDegrees;

    /// <summary>
    /// The share of the ink the engines saw inside a box that is colored. When colored ink was dropped there is
    /// none left to see; without dropping, a share above about a third marks a stamp or pen note.
    /// </summary>
    public double ColoredShare(PixelBox box)
    {
        var rect = new Rect((int)box.Left, (int)box.Top, (int)Math.Max(1, box.Width), (int)Math.Max(1, box.Height))
            .Intersect(new Rect(0, 0, Image.Width, Image.Height));
        if (rect.Width <= 0 || rect.Height <= 0) return 0;
        using var gray = new Mat(Image, rect);
        using var ink = new Mat();
        Cv2.Threshold(gray, ink, 170, 255, ThresholdTypes.BinaryInv);
        var total = Cv2.CountNonZero(ink);
        if (total == 0) return 0;
        using var colored = new Mat(ColoredInk, rect);
        using var both = new Mat();
        Cv2.BitwiseAnd(ink, colored, both);
        return (double)Cv2.CountNonZero(both) / total;
    }

    /// <summary>
    /// The box in page points as the PDF shows the page: undo the deskew rotation and the resampling, then scale
    /// pixels to points. A rotated box becomes the axis-aligned box around its corners.
    /// </summary>
    public BoundingBox ToPagePoints(PixelBox box)
    {
        (double X, double Y)[] corners = [(box.Left, box.Top), (box.Right, box.Top), (box.Right, box.Bottom), (box.Left, box.Bottom)];
        var points = corners.Select(c => (
            X: (toSource[0] * c.X + toSource[1] * c.Y + toSource[2]) * 72 / SourceDpi,
            Y: (toSource[3] * c.X + toSource[4] * c.Y + toSource[5]) * 72 / SourceDpi)).ToList();
        return new BoundingBox(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    /// <summary>The box in points on the straightened page: the frame the document was laid out in.</summary>
    public BoundingBox ToLayoutPoints(PixelBox box) =>
        new(box.Left * 72 / Dpi, box.Top * 72 / Dpi, box.Right * 72 / Dpi, box.Bottom * 72 / Dpi);

    public void Dispose()
    {
        Image.Dispose();
        ColoredInk.Dispose();
        Marks?.Dispose();
    }
}

/// <summary>Image preparation with OpenCV: resample, deskew, despeckle and, optionally, binarize.</summary>
public static class ImagePrep
{
    /// <summary>Prepares a color (BGR) or grayscale page image rendered at <paramref name="sourceDpi"/>.</summary>
    public static PreparedPage Prepare(Mat page, int sourceDpi, PrepOptions options)
    {
        var dpi = options.TargetDpi > 0 ? options.TargetDpi : sourceDpi;
        var scale = (double)dpi / sourceDpi;
        var image = new Mat();
        var chroma = new Mat(page.Size(), MatType.CV_8UC1, Scalar.All(0));
        if (page.Channels() == 3)
        {
            Cv2.CvtColor(page, image, ColorConversionCodes.BGR2GRAY);
            Chroma(page, chroma);
        }
        else
        {
            page.CopyTo(image);
        }

        // OpenCV's geometric transforms can't write over their input, so each step swaps in a new image.
        if (Math.Abs(scale - 1) > 0.001)
        {
            image = Replace(image, (src, dst) => Cv2.Resize(src, dst, new Size(), scale, scale, InterpolationFlags.Cubic));
            chroma = Replace(chroma, (src, dst) => Cv2.Resize(src, dst, image.Size(), 0, 0, InterpolationFlags.Linear));
        }

        var angle = options.Deskew ? EstimateSkew(image) : 0;
        var center = new Point2f(image.Width / 2f, image.Height / 2f);
        using var rotation = Cv2.GetRotationMatrix2D(center, angle, 1);
        if (Math.Abs(angle) >= 0.05)
        {
            image = Replace(image, (src, dst) => Cv2.WarpAffine(src, dst, rotation, src.Size(), InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.All(255)));
            chroma = Replace(chroma, (src, dst) => Cv2.WarpAffine(src, dst, rotation, src.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0)));
        }

        // Colored ink: channels that differ by more than 60 levels (red stamps, blue pen), not black, gray or paper.
        var colored = new Mat();
        Cv2.Threshold(chroma, colored, 60, 255, ThresholdTypes.Binary);
        Mat? marks = null;
        if (options.DropColoredInk && Cv2.CountNonZero(colored) > 0)
        {
            // Dropping only the strong color leaves a faint halo that an engine can still read, so the drop mask
            // takes any visible tint and one pixel around it. Marks keep the ink's gray levels on white.
            using var drop = new Mat();
            Cv2.Threshold(chroma, drop, 25, 255, ThresholdTypes.Binary);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.Dilate(drop, drop, kernel);
            marks = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(255));
            image.CopyTo(marks, drop);

            // Fill from the surroundings, not with white: a white stamp on off-white paper or on a shaded table
            // header is still a readable stamp, as PaddleOCR shows.
            image = Replace(image, (src, dst) => Cv2.Inpaint(src, drop, dst, 3, InpaintTypes.Telea));
        }

        chroma.Dispose();

        if (options.Despeckle) Despeckle(image);
        if (options.Binarize) image = Replace(image, (src, dst) => Cv2.AdaptiveThreshold(src, dst, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 31, 15));

        // Prepared pixels back to source pixels: undo the rotation, then the resampling.
        using var inverse = new Mat();
        Cv2.InvertAffineTransform(rotation, inverse);
        inverse.GetArray(out double[] m);
        double[] toSource = [m[0] / scale, m[1] / scale, m[2] / scale, m[3] / scale, m[4] / scale, m[5] / scale];
        return new PreparedPage(image, colored, marks, dpi, sourceDpi, Math.Abs(angle) >= 0.05 ? angle : 0, toSource);
    }

    private static Mat Replace(Mat source, Action<Mat, Mat> transform)
    {
        var result = new Mat();
        transform(source, result);
        source.Dispose();
        return result;
    }

    /// <summary>Chroma: the largest channel minus the smallest. Black, gray and paper are near zero; stamps and pen are high.</summary>
    private static void Chroma(Mat bgr, Mat chroma)
    {
        var channels = Cv2.Split(bgr);
        try
        {
            using var max = new Mat();
            using var min = new Mat();
            Cv2.Max(channels[0], channels[1], max);
            Cv2.Max(max, channels[2], max);
            Cv2.Min(channels[0], channels[1], min);
            Cv2.Min(min, channels[2], min);
            Cv2.Subtract(max, min, chroma);
        }
        finally
        {
            foreach (var c in channels) c.Dispose();
        }
    }

    /// <summary>
    /// The rotation, in degrees counterclockwise, that makes the text lines horizontal. Ink is summed along each
    /// row for every candidate angle; level lines give tall peaks and empty gaps, so the profile with the largest
    /// sum of squares wins. A coarse search finds the neighborhood and a fine one the angle.
    /// </summary>
    public static double EstimateSkew(Mat gray, double maxDegrees = 5)
    {
        using var small = new Mat();
        var f = Math.Min(1, 2000.0 / Math.Max(gray.Width, gray.Height));
        Cv2.Resize(gray, small, new Size(), f, f, InterpolationFlags.Area);
        using var ink = new Mat();
        Cv2.Threshold(small, ink, 0, 1, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        var best = 0.0;
        foreach (var (range, step) in new[] { (maxDegrees, 0.5), (0.5, 0.05) })
        {
            var center = best;
            var bestScore = double.MinValue;
            for (var a = center - range; a <= center + range + 1e-9; a += step)
            {
                var score = ProfileScore(ink, a);
                if (score > bestScore) (bestScore, best) = (score, a);
            }
        }

        return Math.Round(best, 2);
    }

    private static double ProfileScore(Mat ink, double angle)
    {
        using var rotation = Cv2.GetRotationMatrix2D(new Point2f(ink.Width / 2f, ink.Height / 2f), angle, 1);
        using var rotated = new Mat();
        Cv2.WarpAffine(ink, rotated, rotation, ink.Size(), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
        using var rows = new Mat();
        Cv2.Reduce(rotated, rows, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_64F);
        rows.GetArray(out double[] sums);
        return sums.Sum(s => s * s);
    }

    /// <summary>
    /// Removes specks: ink blobs of a few pixels with no other ink nearby. A blob next to text (the dot of an "i",
    /// the middle dot in "N·m", a full stop) is kept, because dropping it changes the text.
    /// </summary>
    public static int Despeckle(Mat gray, int maxArea = 9, int radius = 6)
    {
        using var ink = new Mat();
        Cv2.Threshold(gray, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(ink, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var small = new bool[count];
        for (var i = 1; i < count; i++) small[i] = stats.At<int>(i, (int)ConnectedComponentsTypes.Area) <= maxArea;

        labels.GetArray(out int[] label);
        var large = new byte[label.Length];
        for (var p = 0; p < label.Length; p++) if (label[p] > 0 && !small[label[p]]) large[p] = 255;
        using var near = Mat.FromPixelData(gray.Rows, gray.Cols, MatType.CV_8UC1, large);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * radius + 1, 2 * radius + 1));
        Cv2.Dilate(near, near, kernel);
        near.GetArray(out byte[] nearText);

        var keep = new bool[count];
        for (var p = 0; p < label.Length; p++) if (small[label[p]] && nearText[p] != 0) keep[label[p]] = true;
        gray.GetArray(out byte[] pixels);
        var removed = new HashSet<int>();
        for (var p = 0; p < label.Length; p++)
        {
            var l = label[p];
            if (l > 0 && small[l] && !keep[l])
            {
                pixels[p] = 255;
                removed.Add(l);
            }
        }

        gray.SetArray(pixels);
        return removed.Count;
    }
}
