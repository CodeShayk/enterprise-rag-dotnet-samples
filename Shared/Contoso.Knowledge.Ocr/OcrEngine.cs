using OpenCvSharp;

namespace Contoso.Knowledge.Ocr;

/// <summary>A box in image pixels, origin at the top left.</summary>
public readonly record struct PixelBox(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public double CenterX => (Left + Right) / 2;

    public double CenterY => (Top + Bottom) / 2;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public static PixelBox Union(IEnumerable<PixelBox> boxes)
    {
        var list = boxes.ToList();
        return new(list.Min(b => b.Left), list.Min(b => b.Top), list.Max(b => b.Right), list.Max(b => b.Bottom));
    }
}

/// <summary>A recognized word with the engine's confidence, from 0 to 1.</summary>
public sealed record OcrWord(string Text, PixelBox Box, double Confidence);

/// <summary>A recognized line: the unit both engines return reliably.</summary>
public sealed record OcrLine(string Text, PixelBox Box, double Confidence, IReadOnlyList<OcrWord> Words);

/// <summary>What an engine read on one page image.</summary>
public sealed record OcrPage(int Width, int Height, IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join('\n', Lines.Select(l => l.Text));

    /// <summary>The mean word confidence, weighted by word length.</summary>
    public double MeanConfidence
    {
        get
        {
            var words = Lines.SelectMany(l => l.Words).ToList();
            var chars = words.Sum(w => w.Text.Length);
            return chars == 0 ? 0 : words.Sum(w => w.Confidence * w.Text.Length) / chars;
        }
    }
}

/// <summary>An OCR engine. Engines aren't thread-safe: use one instance per worker.</summary>
public interface IOcrEngine : IDisposable
{
    /// <summary>A short name, such as <c>tesseract-best</c>, recorded with every block the engine produced.</summary>
    string Name { get; }

    /// <summary>Reads a grayscale page image.</summary>
    OcrPage Recognize(Mat image);

    /// <summary>Reads a small crop, such as one table cell, as a single line or block of text.</summary>
    OcrPage RecognizeRegion(Mat image) => Recognize(image);
}
