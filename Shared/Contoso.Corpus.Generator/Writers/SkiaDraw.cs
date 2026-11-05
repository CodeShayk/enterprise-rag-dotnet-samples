using SkiaSharp;

namespace Contoso.Corpus.Generator.Writers;

/// <summary>Draws layout operations on a Skia canvas: a raster page for scans or a PDF page for born-digital files.</summary>
internal static class SkiaDraw
{
    public static void Draw(SKCanvas canvas, DrawOp op, bool wordFragments)
    {
        switch (op)
        {
            case TextOp t:
                using (var font = new SKFont(Metrics.Face(t.Font), (float)t.Size) { Edging = SKFontEdging.Antialias })
                using (var paint = new SKPaint { Color = Color(t.Color), IsAntialias = true })
                {
                    if (wordFragments && t.Text.Contains(' '))
                    {
                        // Each word as its own text object, positioned by measured widths, as some producers write them.
                        var x = (float)t.X;
                        foreach (var word in t.Text.Split(' '))
                        {
                            canvas.DrawText(word, x, (float)t.Baseline, SKTextAlign.Left, font, paint);
                            x += font.MeasureText(word + " ");
                        }
                    }
                    else
                    {
                        canvas.DrawText(t.Text, (float)t.X, (float)t.Baseline, SKTextAlign.Left, font, paint);
                    }
                }

                break;
            case LineOp l:
                using (var paint = new SKPaint { Color = Color(l.Color), StrokeWidth = (float)l.Width, IsStroke = true, IsAntialias = true })
                {
                    canvas.DrawLine((float)l.X1, (float)l.Y1, (float)l.X2, (float)l.Y2, paint);
                }

                break;
            case RectOp r:
                using (var paint = new SKPaint { Color = Color(r.Color), StrokeWidth = (float)r.LineWidth, IsStroke = !r.Fill, IsAntialias = true })
                {
                    canvas.DrawRect((float)r.X, (float)r.Y, (float)r.W, (float)r.H, paint);
                }

                break;
            case EllipseOp e:
                using (var paint = new SKPaint { Color = Color(e.Color), StrokeWidth = (float)e.LineWidth, IsStroke = true, IsAntialias = true })
                {
                    canvas.DrawCircle((float)e.CenterX, (float)e.CenterY, (float)e.Diameter / 2, paint);
                }

                break;
        }
    }

    public static SKColor Color(Rgb c) => new(c.R, c.G, c.B);
}
