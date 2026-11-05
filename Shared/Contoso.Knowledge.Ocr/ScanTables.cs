using Contoso.Knowledge.Documents;
using OpenCvSharp;

namespace Contoso.Knowledge.Ocr;

/// <summary>A ruled table on a page image: the positions of its row and column lines, in pixels.</summary>
public sealed record TableGrid(PixelBox Box, IReadOnlyList<double> Rows, IReadOnlyList<double> Columns)
{
    public int RowCount => Rows.Count - 1;

    public int ColumnCount => Columns.Count - 1;

    public PixelBox Cell(int row, int column) => new(Columns[column], Rows[row], Columns[column + 1], Rows[row + 1]);
}

/// <summary>A table read from a scan, with the confidence of every cell and the cells that were read twice.</summary>
public sealed record ScanTable(TableGrid Grid, TableBlock Block, double[,] Confidence, int Reread);

/// <summary>
/// Tables from scans. Detection keeps long horizontal and vertical strokes with line-shaped morphological
/// openings and groups them into grids; structure comes from where the lines are; each word goes to the cell
/// that holds its center, and a cell the page OCR read poorly is cropped and read again on its own.
/// </summary>
public static class ScanTables
{
    public static IReadOnlyList<TableGrid> Find(Mat gray)
    {
        using var ink = new Mat();
        Cv2.AdaptiveThreshold(gray, ink, 255, AdaptiveThresholdTypes.MeanC, ThresholdTypes.BinaryInv, 25, 15);
        using var horizontal = Strokes(ink, new Size(Math.Max(40, gray.Width / 30), 1));
        using var vertical = Strokes(ink, new Size(1, Math.Max(20, gray.Height / 80)));
        using var grid = new Mat();
        Cv2.BitwiseOr(horizontal, vertical, grid);
        Cv2.FindContours(grid, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var tables = new List<TableGrid>();
        foreach (var rect in contours.Select(c => Cv2.BoundingRect(c)).Where(r => r.Width > gray.Width / 5 && r.Height > 30))
        {
            var rows = Lines(horizontal, rect, alongRows: true);
            var columns = Lines(vertical, rect, alongRows: false);
            // A frame around a figure is one cell; a table has at least two rows and two columns.
            if (rows.Count >= 3 && columns.Count >= 3)
            {
                tables.Add(new TableGrid(new PixelBox(rect.Left, rect.Top, rect.Right, rect.Bottom), rows, columns));
            }
        }

        return [.. tables.OrderBy(t => t.Box.Top)];
    }

    /// <summary>
    /// Fills the grid's cells with the page's OCR words. Cells below <paramref name="threshold"/>, or empty, are read
    /// again from their own crop, enlarged, and the more confident reading wins.
    /// </summary>
    public static ScanTable Read(TableGrid grid, IReadOnlyList<OcrWord> words, Mat image, IOcrEngine engine, double threshold = 0.8, bool postCorrect = true)
    {
        var rows = new List<IReadOnlyList<string>>();
        var confidence = new double[grid.RowCount, grid.ColumnCount];
        var reread = 0;
        for (var r = 0; r < grid.RowCount; r++)
        {
            var cells = new List<string>();
            for (var c = 0; c < grid.ColumnCount; c++)
            {
                var box = grid.Cell(r, c);
                var inside = words.Where(w => box.Contains(w.Box.CenterX, w.Box.CenterY)).ToList();
                var text = Join(inside);
                var score = inside.Count == 0 ? 0 : inside.Sum(w => w.Confidence * w.Text.Length) / inside.Sum(w => w.Text.Length);
                if (score < threshold && CropRead(image, box, engine) is { } second && second.Confidence > score)
                {
                    (text, score) = (second.Text, second.Confidence);
                    reread++;
                }

                cells.Add(OcrText.Cell(postCorrect ? OcrText.Correct(text) : text));
                confidence[r, c] = score;
            }

            rows.Add(cells);
        }

        return new ScanTable(grid, new TableBlock(rows, HeaderRows: 1), confidence, reread);
    }

    private static string Join(IReadOnlyList<OcrWord> words)
    {
        // Lines inside the cell top to bottom, words left to right: a cell may wrap.
        var lines = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(w => w.Box.CenterY))
        {
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs(w.Box.CenterY - line.Average(x => x.Box.CenterY)) < 0.5 * w.Box.Height) line.Add(w);
            else lines.Add([w]);
        }

        return string.Join(' ', lines.SelectMany(l => l.OrderBy(w => w.Box.Left)).Select(w => w.Text));
    }

    private static (string Text, double Confidence)? CropRead(Mat image, PixelBox cell, IOcrEngine engine)
    {
        // Stay inside the ruling lines, and enlarge: engines read small isolated text better at twice the size.
        var rect = new Rect((int)cell.Left + 4, (int)cell.Top + 4, (int)cell.Width - 8, (int)cell.Height - 8)
            .Intersect(new Rect(0, 0, image.Width, image.Height));
        if (rect.Width < 8 || rect.Height < 8) return null;
        using var crop = new Mat(image, rect);
        using var large = new Mat();
        Cv2.Resize(crop, large, new Size(), 2, 2, InterpolationFlags.Cubic);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(large, padded, 20, 20, 20, 20, BorderTypes.Replicate);
        var page = engine.RecognizeRegion(padded);
        return page.Lines.Count == 0 ? null : (string.Join(' ', page.Lines.Select(l => l.Text)), page.MeanConfidence);
    }

    private static Mat Strokes(Mat ink, Size kernel)
    {
        using var element = Cv2.GetStructuringElement(MorphShapes.Rect, kernel);
        var strokes = new Mat();
        Cv2.MorphologyEx(ink, strokes, MorphTypes.Open, element);
        return strokes;
    }

    /// <summary>The centers of the lines that run across most of the table, as positions on the page.</summary>
    private static List<double> Lines(Mat strokes, Rect rect, bool alongRows)
    {
        using var roi = new Mat(strokes, rect);
        using var sums = new Mat();
        Cv2.Reduce(roi, sums, alongRows ? ReduceDimension.Column : ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_64F);
        sums.GetArray(out double[] s);
        var span = alongRows ? rect.Width : rect.Height;
        var origin = alongRows ? rect.Top : rect.Left;
        var lines = new List<double>();
        int? start = null;
        for (var i = 0; i <= s.Length; i++)
        {
            var on = i < s.Length && s[i] >= 0.6 * span * 255;
            if (on && start is null) start = i;
            if (!on && start is { } first)
            {
                lines.Add(origin + (first + i - 1) / 2.0);
                start = null;
            }
        }

        return lines;
    }
}
