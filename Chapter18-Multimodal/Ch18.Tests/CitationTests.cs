using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Vision;
using SkiaSharp;

namespace Ch18.Tests;

public sealed class LocateTests
{
    private static PageWord W(string text, double left, double top, double width = 30, double height = 10) =>
        new(text, new BoundingBox(left, top, left + width, top + height));

    [Fact]
    public void A_quote_runs_from_the_end_of_a_line_to_the_line_below()
    {
        PageWord[] words = [W("Tighten", 50, 100), W("the", 85, 100), W("M12", 120, 100), W("bolts", 50, 114), W("to", 85, 114), W("85", 120, 114), W("N-m.", 155, 114)];

        // OCR read the middle dot as a hyphen; letters and digits still match.
        Assert.Equal(new BoundingBox(50, 100, 185, 124), PageWords.Locate(words, "the M12 bolts to 85 N·m."));
    }

    [Fact]
    public void A_skewed_line_still_reads_as_one_line()
    {
        // Each word sits two points lower than the one before it, as on a scan turned by about two degrees.
        PageWord[] words = [W("Replace", 50, 200), W("the", 85, 202), W("bearings", 120, 204), W("every", 155, 206), W("32,000", 190, 208), W("h", 225, 210, 8)];

        Assert.Equal(new BoundingBox(120, 204, 233, 220), PageWords.Locate(words, "bearings every 32,000 h"));
    }

    [Fact]
    public void A_column_gutter_ends_a_line()
    {
        PageWord[] words = [W("Seal", 50, 300), W("water", 85, 300), W("pressure", 330, 300), W("low", 365, 300)];

        Assert.Null(PageWords.Locate(words, "Seal water pressure low"));
        Assert.Equal(2, PageWords.Lines(words).Count);
    }

    [Fact]
    public void A_row_quoted_with_its_column_names_is_found_by_its_cells()
    {
        PageWord[] words = [W("M10", 60, 280), W("49", 300, 280), W("N·m", 320, 280), W("M12", 60, 300), W("85", 300, 300), W("N·m", 320, 300)];

        Assert.Equal(new BoundingBox(60, 300, 350, 310), PageWords.Locate(words, "Bolt size: M12; Torque: 85 N·m"));
    }
}

public sealed class NumericCheckTests
{
    private const string Source = "Tighten the M12 flange bolts (property class 8.8, lightly oiled) to 85 N·m.";

    [Fact]
    public void The_same_quantity_in_another_spelling_passes() => Assert.Empty(NumericCheck.Check("The M12 bolts take 85 Nm.", Source));

    [Fact]
    public void A_number_the_source_does_not_state_fails() =>
        Assert.Equal("not in the source", Assert.Single(NumericCheck.Check("The M12 bolts take 58 N·m.", Source)).Problem);

    [Fact]
    public void The_right_number_in_another_unit_fails() =>
        Assert.Equal("the source says 85 N·m", Assert.Single(NumericCheck.Check("The gap is 85 mm.", Source)).Problem);

    [Fact]
    public void Numbers_the_question_gave_count_as_known()
    {
        Assert.Single(NumericCheck.Check("Size 50 bolts take 85 N·m.", Source));
        Assert.Empty(NumericCheck.Check("Size 50 bolts take 85 N·m.", Source, "What torque do size 50 bolts take?"));
    }

    [Fact]
    public void Identifiers_are_not_quantities() =>
        Assert.Equal([new Quantity(2000, "h", "2,000 operating hours")], NumericCheck.Quantities("Inspect PN-4471-C on the CP-200 every 2,000 operating hours."));
}

public sealed class VisualCitationTests
{
    [Fact]
    public void A_citation_is_a_deep_link_in_whole_points()
    {
        var citation = new VisualCitation("CP200-MAN-2009", 3, new BoundingBox(57.2, 378.4, 391, 393));

        Assert.Equal("/documents/CP200-MAN-2009/pages/3?box=57,378,334,15", citation.Link);
        Assert.Equal(new BoundingBox(57, 378, 391, 393), VisualCitation.Parse("CP200-MAN-2009", 3, "57,378,334,15")!.Box);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("57,378,334")]
    [InlineData("57,378,0,15")]
    [InlineData("a,b,c,d")]
    public void A_malformed_box_is_no_citation(string? box) => Assert.Null(VisualCitation.Parse("CP200-MAN-2009", 3, box));

    [Fact]
    public void The_highlight_washes_the_region_and_nothing_else()
    {
        var pdf = File.ReadAllBytes(GeneratedCorpus.FullPath(GeneratedCorpus.Register().Single(e => e.Id == "CP200-MAN-2009" && e.CopyOf is null)));
        using var plain = SKBitmap.Decode(PageRenders.Highlight(pdf, 3, []));
        using var marked = SKBitmap.Decode(PageRenders.Highlight(pdf, 3, [new BoundingBox(545, 400, 580, 420)]));

        // At 110 DPI, the region's center (562, 410) pt is pixel (859, 626); the page's top left corner stays as it was.
        Assert.True(marked.GetPixel(859, 626).Blue < plain.GetPixel(859, 626).Blue - 40);
        Assert.Equal(plain.GetPixel(20, 20), marked.GetPixel(20, 20));
    }
}
