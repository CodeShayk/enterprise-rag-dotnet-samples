using SkiaSharp;

namespace Contoso.Corpus.Generator.Writers;

/// <summary>What the PDF's information dictionary says, with fixed dates so that output is deterministic.</summary>
internal sealed record PdfInfo(string Title, string Author, DateTimeOffset Created);

/// <summary>
/// Writes laid-out pages as a born-digital PDF through Skia's PDF backend, which embeds font subsets with
/// ToUnicode maps, so text extracts as it was written. Pages listed in <c>scannedPages</c> are rasterized with
/// <see cref="ScanRenderer"/> and embedded as JPEG images with no text layer.
/// </summary>
internal static class PdfRenderer
{
    public static byte[] Render(
        IReadOnlyList<LaidOutPage> pages, PageStyle style, PdfInfo info,
        ISet<int>? scannedPages = null, ScanStyle? scan = null)
    {
        using var stream = new MemoryStream();
        var metadata = new SKDocumentPdfMetadata
        {
            Title = info.Title,
            Author = info.Author,
            Creator = info.Author,
            Producer = "Contoso document services",
            Creation = info.Created.UtcDateTime,
            Modified = info.Created.UtcDateTime,
            EncodingQuality = 95,
        };

        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            foreach (var laid in pages)
            {
                var canvas = document.BeginPage((float)style.Width, (float)style.Height);
                if (scannedPages?.Contains(laid.Number) == true)
                {
                    var jpeg = ScanRenderer.Render(laid, style, scan ?? ScanStyle.Default, seed: laid.Number);
                    using var image = SKImage.FromEncodedData(jpeg);
                    canvas.DrawImage(image, SKRect.Create(0, 0, (float)style.Width, (float)style.Height), new SKSamplingOptions(SKFilterMode.Linear));
                }
                else
                {
                    var ops = laid.Ops;
                    if (style.RowMajorContent)
                    {
                        // Some producers write text line by line across the whole page, so the content stream
                        // interleaves the columns. Reading order then has to be recovered from positions (Chapter 7).
                        ops = [.. ops.OrderBy(o => o is TextOp t ? Math.Round(t.Baseline / 2) : -1).ThenBy(o => o is TextOp t ? t.X : 0)];
                    }

                    foreach (var op in ops)
                    {
                        SkiaDraw.Draw(canvas, op, style.WordFragments);
                    }
                }

                document.EndPage();
            }

            document.Close();
        }

        return stream.ToArray();
    }
}
