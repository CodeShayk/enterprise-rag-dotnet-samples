using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Ocr;
using OpenCvSharp;

namespace Ch08.Tests;

/// <summary>Image preparation and coordinates, on pages drawn in code.</summary>
public sealed class PreparationTests
{
    [Fact]
    public void Deskew_finds_the_rotation_that_levels_the_lines()
    {
        using var page = TextPage();
        using var rotated = Rotate(page, -1.5);   // clockwise, as a page fed crookedly into a scanner

        var correction = ImagePrep.EstimateSkew(rotated);

        Assert.InRange(correction, 1.35, 1.65);
    }

    [Fact]
    public void Despeckle_removes_isolated_specks_and_keeps_dots_next_to_text()
    {
        using var page = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(250));
        Cv2.Rectangle(page, new Rect(100, 100, 30, 40), Scalar.All(0), -1);   // a letter
        Cv2.Rectangle(page, new Rect(134, 118, 3, 3), Scalar.All(0), -1);     // a middle dot beside it
        Cv2.Rectangle(page, new Rect(300, 300, 2, 2), Scalar.All(0), -1);     // a speck on its own

        var removed = ImagePrep.Despeckle(page);

        Assert.Equal(1, removed);
        Assert.True(page.At<byte>(119, 135) < 100, "the dot next to the letter stays");
        Assert.True(page.At<byte>(300, 300) > 200, "the isolated speck goes");
    }

    [Fact]
    public void Colored_ink_is_dropped_from_the_page_and_kept_as_marks()
    {
        using var page = new Mat(300, 600, MatType.CV_8UC3, new Scalar(234, 242, 244));   // paper, in BGR
        Cv2.Rectangle(page, new Rect(50, 100, 150, 40), Scalar.All(10), -1);              // black print
        Cv2.Rectangle(page, new Rect(350, 100, 150, 40), new Scalar(40, 30, 190), -1);    // a red stamp

        using var prepared = ImagePrep.Prepare(page, 300, PrepOptions.None with { DropColoredInk = true });

        Assert.NotNull(prepared.Marks);
        Assert.True(Mean(prepared.Image, new Rect(360, 110, 130, 20)) > 200, "the stamp is gone from the page the engine reads");
        Assert.True(Mean(prepared.Marks!, new Rect(360, 110, 130, 20)) < 150, "and kept among the marks");
        Assert.True(Mean(prepared.Image, new Rect(60, 110, 130, 20)) < 50, "black print stays");
    }

    [Fact]
    public void Boxes_map_back_to_page_points_through_resampling()
    {
        using var page = new Mat(1650, 1275, MatType.CV_8UC1, Scalar.All(255));   // US Letter at 150 DPI

        using var prepared = ImagePrep.Prepare(page, 150, PrepOptions.None with { TargetDpi = 300 });
        var box = prepared.ToPagePoints(new PixelBox(300, 600, 900, 660));

        Assert.Equal(new BoundingBox(72, 144, 216, 158.4), Round(box));
    }

    [Fact]
    public void A_rotated_box_maps_to_the_box_around_its_corners()
    {
        using var page = TextPage();
        using var rotated = Rotate(page, -2);
        using var prepared = ImagePrep.Prepare(rotated, 300, PrepOptions.None with { TargetDpi = 300, Deskew = true });

        // A line-wide box on the straightened page covers a taller box on the crooked scan the viewer shows.
        var line = new PixelBox(100, 1000, 1600, 1040);
        var layout = prepared.ToLayoutPoints(line);
        var shown = prepared.ToPagePoints(line);

        // Rotating a w-by-h box by θ gives a bounding box w·sin θ + h·cos θ high: 360 pt wide, 2°, 9.6 pt high.
        var expected = layout.Width * Math.Sin(2 * Math.PI / 180) + layout.Height * Math.Cos(2 * Math.PI / 180);
        Assert.InRange(prepared.SkewDegrees, 1.85, 2.15);
        Assert.InRange(shown.Height, expected - 1, expected + 1);
    }

    internal static Mat TextPage()
    {
        var page = new Mat(2200, 1700, MatType.CV_8UC1, Scalar.All(255));
        for (var i = 0; i < 40; i++)
        {
            Cv2.PutText(page, "Tighten the M12 flange bolts to 85 Nm in three passes.", new Point(100, 120 + i * 50),
                HersheyFonts.HersheySimplex, 1.2, Scalar.All(0), 2, LineTypes.AntiAlias);
        }

        return page;
    }

    private static Mat Rotate(Mat page, double degrees)
    {
        using var rotation = Cv2.GetRotationMatrix2D(new Point2f(page.Width / 2f, page.Height / 2f), degrees, 1);
        var rotated = new Mat();
        Cv2.WarpAffine(page, rotated, rotation, page.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255));
        return rotated;
    }

    private static double Mean(Mat image, Rect rect)
    {
        using var roi = new Mat(image, rect);
        return Cv2.Mean(roi).Val0;
    }

    private static BoundingBox Round(BoundingBox b) => new(Math.Round(b.Left, 1), Math.Round(b.Top, 1), Math.Round(b.Right, 1), Math.Round(b.Bottom, 1));
}

/// <summary>Tables, error rates, alignment, post-correction and triage.</summary>
public sealed class StructureAndQualityTests
{
    [Fact]
    public void A_ruled_grid_is_found_with_its_rows_and_columns()
    {
        using var page = new Mat(1200, 1000, MatType.CV_8UC1, Scalar.All(250));
        foreach (var y in new[] { 300, 360, 420, 480 }) Cv2.Line(page, new Point(100, y), new Point(800, y), Scalar.All(0), 3);
        foreach (var x in new[] { 100, 400, 800 }) Cv2.Line(page, new Point(x, 300), new Point(x, 480), Scalar.All(0), 3);

        var grid = Assert.Single(ScanTables.Find(page));

        Assert.Equal(3, grid.RowCount);
        Assert.Equal(2, grid.ColumnCount);
        Assert.InRange(grid.Cell(1, 1).Left, 398, 402);
    }

    [Fact]
    public void Error_rates_count_edits_against_the_reference()
    {
        Assert.Equal(1.0 / 6, OcrQuality.Cer("85 N·m", "85 N-m"), 3);
        Assert.Equal(0.5, OcrQuality.Wer("85 N·m", "85 N-m"), 3);
        Assert.Equal(0, OcrQuality.Cer("• Isolate the motor.", "Isolate the motor."));
    }

    [Fact]
    public void Alignment_marks_substituted_and_inserted_words_as_wrong()
    {
        var matched = OcrQuality.MatchedWords(["Fit", "PN-4471-C", "now"], ["Fit", "|", "PN-4471-G", "now"]);

        Assert.Equal([true, false, false, true], matched);
    }

    [Theory]
    [InlineData("Tighten the bolts to 85 N-m.", "Tighten the bolts to 85 N·m.")]
    [InlineData("then 85 N'm. Re-check", "then 85 N·m. Re-check")]
    [InlineData("4. 1 Foundation and baseplate", "4.1 Foundation and baseplate")]
    [InlineData("Order PN-4471-C from Fabrikam Seals.", "Order PN-4471-C from Fabrikam Seals.")]
    [InlineData("1. Check that the rotating assembly turns", "1. Check that the rotating assembly turns")]
    public void Post_correction_changes_only_what_its_rules_name(string read, string expected)
    {
        Assert.Equal(expected, OcrText.Correct(read));
    }

    [Fact]
    public void Triage_counts_private_use_and_replacement_characters_as_unreadable()
    {
        Assert.True(Triage.Readable('N'));
        Assert.True(Triage.Readable('·'));
        Assert.False(Triage.Readable(''));
        Assert.False(Triage.Readable('�'));
    }
}
