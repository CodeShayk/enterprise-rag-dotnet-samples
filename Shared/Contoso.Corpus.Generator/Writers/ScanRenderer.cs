using SkiaSharp;

namespace Contoso.Corpus.Generator.Writers;

/// <summary>A mark added on paper after printing: a rubber stamp or handwriting, on page <see cref="Page"/>.</summary>
internal sealed record PaperMark(int Page, string Text, double X, double Y, double Angle, Rgb Color, bool Stamp = true, double Size = 15);

/// <summary>How a page looks after it has been printed, marked and scanned.</summary>
internal sealed record ScanStyle
{
    public static ScanStyle Default { get; } = new();

    public int Dpi { get; init; } = 300;

    /// <summary>The largest skew in degrees; each page gets a deterministic angle up to this size.</summary>
    public double SkewDegrees { get; init; } = 1.2;

    /// <summary>Speckle density: dust and toner specks.</summary>
    public double Noise { get; init; } = 0.6;

    public float Blur { get; init; } = 0.7f;

    public int JpegQuality { get; init; } = 70;

    public IReadOnlyList<PaperMark> Marks { get; init; } = [];
}

/// <summary>Rasterizes a laid-out page and makes it look scanned: skew, blur, paper tone, specks, stamps and handwriting.</summary>
internal static class ScanRenderer
{
    public static byte[] Render(LaidOutPage page, PageStyle style, ScanStyle scan, int seed)
    {
        var scale = scan.Dpi / 72f;
        var w = (int)Math.Round(style.Width * scale);
        var h = (int)Math.Round(style.Height * scale);
        using var clean = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(clean))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(scale);
            foreach (var op in page.Ops)
            {
                SkiaDraw.Draw(canvas, op, wordFragments: false);
            }

            foreach (var mark in scan.Marks.Where(m => m.Page == page.Number))
            {
                DrawMark(canvas, mark);
            }
        }

        var random = new Random(seed * 7919 + scan.Dpi);
        var angle = scan.SkewDegrees * (random.NextDouble() < 0.5 ? -1 : 1) * (0.6 + 0.4 * random.NextDouble());
        using var scanned = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(scanned))
        {
            canvas.Clear(new SKColor(244, 242, 234));
            using var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(scan.Blur, scan.Blur), BlendMode = SKBlendMode.Multiply };
            canvas.Save();
            canvas.RotateDegrees((float)angle, w / 2f, h / 2f);
            using (var image = SKImage.FromBitmap(clean))
            {
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), blur);
            }

            canvas.Restore();

            using var speck = new SKPaint { IsAntialias = false };
            var count = (int)(w * (long)h * 0.00035 * scan.Noise);
            for (var i = 0; i < count; i++)
            {
                var gray = (byte)random.Next(40, 190);
                speck.Color = new SKColor(gray, gray, gray);
                canvas.DrawRect(random.Next(w), random.Next(h), random.Next(1, 4), random.Next(1, 4), speck);
            }

            // The darker band a flatbed scanner leaves along one edge.
            using var edge = new SKPaint { Color = new SKColor(0, 0, 0, 28) };
            canvas.DrawRect(0, 0, 18 * scale / 4, h, edge);
        }

        using var encoded = SKImage.FromBitmap(scanned);
        using var data = encoded.Encode(SKEncodedImageFormat.Jpeg, scan.JpegQuality);
        return data.ToArray();
    }

    private static void DrawMark(SKCanvas canvas, PaperMark mark)
    {
        canvas.Save();
        canvas.Translate((float)mark.X, (float)mark.Y);
        canvas.RotateDegrees((float)mark.Angle);
        var color = Color(mark.Color).WithAlpha(mark.Stamp ? (byte)200 : (byte)230);
        using var font = new SKFont(Metrics.Face(mark.Stamp ? FontKind.Bold : FontKind.Script), (float)mark.Size);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var lines = mark.Text.Split('\n');
        var width = lines.Max(l => font.MeasureText(l));
        var lineHeight = (float)mark.Size * 1.2f;
        if (mark.Stamp)
        {
            using var border = new SKPaint { Color = color, IsStroke = true, StrokeWidth = 2.2f, IsAntialias = true };
            canvas.DrawRoundRect(-8, -(float)mark.Size - 6, width + 16, lineHeight * lines.Length + 10, 4, 4, border);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            canvas.DrawText(lines[i], 0, i * lineHeight, SKTextAlign.Left, font, paint);
        }

        canvas.Restore();
    }

    private static SKColor Color(Rgb c) => SkiaDraw.Color(c);
}
