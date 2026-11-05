using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Ocr;

namespace Ch08.Tests;

/// <summary>End to end on the generated corpus: the answer to signature question 1 lives only on a scan.</summary>
public sealed class ScannedManualTests
{
    [Fact]
    public async Task The_2009_manual_gives_the_M12_torque_from_its_scan()
    {
        if (!File.Exists(Path.Combine(OcrModels.Root, "tesseract", "best", "eng.traineddata")))
        {
            Assert.Skip("Run python eng/get-models.py tesseract-eng-best first.");
        }

        var entry = GeneratedCorpus.Register().Single(e => e.Path.EndsWith("CP-200 Manual 2009.pdf", StringComparison.Ordinal));
        using var engine = new TesseractEngine("best");
        var parser = new ScannedPdfParser(engine);
        await using var file = File.OpenRead(GeneratedCorpus.FullPath(entry));

        var parsed = await parser.ParseAsync(file, new ParseContext("CP-200 Manual 2009.pdf", MediaTypes.Pdf), TestContext.Current.CancellationToken);

        var torque = parsed.Blocks.OfType<TableBlock>().Single(t => t.Rows.Any(r => r[0] == "M12"));
        Assert.Equal(["M12", "85 N·m"], torque.Rows.Single(r => r[0] == "M12"));
        Assert.Equal(3, torque.Anchor!.Page);
        Assert.InRange(torque.Anchor.Box!.Value.Top, 280, 300);   // the truth puts Table 6-2 at 294.5 pt
        Assert.Contains(parser.LastReview, r => r.Reason == "mark on paper");
    }
}
