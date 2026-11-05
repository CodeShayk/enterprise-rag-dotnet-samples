using OpenCvSharp;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Contoso.Knowledge.Ocr;

/// <summary>Page images: PDFium renders the page, OpenCV takes it from there.</summary>
public static class PageImages
{
    /// <summary>Renders a page (numbered from 1) at the given resolution as a color image (BGR), which keeps stamps and ink colors.</summary>
    public static Mat Render(byte[] pdf, int pageNumber, int dpi)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            throw new PlatformNotSupportedException("PDFium rendering needs Windows, Linux or macOS.");
        }

        // The guard above covers PDFium's desktop platforms; the analyzer can't combine three platform checks.
#pragma warning disable CA1416
        using var bitmap = Conversion.ToImage(pdf, pageNumber - 1, options: new RenderOptions(Dpi: dpi));
#pragma warning restore CA1416
        return ToMat(bitmap);
    }

    /// <summary>Copies a Skia bitmap into an OpenCV color image (BGR) without encoding it.</summary>
    public static Mat ToMat(SKBitmap bitmap)
    {
        using var bgra = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        var source = bgra ?? bitmap;
        using var view = Mat.FromPixelData(source.Height, source.Width, MatType.CV_8UC4, source.GetPixels(), source.RowBytes);
        var color = new Mat();
        Cv2.CvtColor(view, color, ColorConversionCodes.BGRA2BGR);
        return color;
    }

    /// <summary>
    /// The resolution a scanned page was captured at: the largest image's width in pixels over its width on the
    /// page in inches. Rendering above it adds pixels but no detail; <see langword="null"/> when the page has no image.
    /// </summary>
    public static int? NativeDpi(byte[] pdf, int pageNumber)
    {
        using var document = PdfDocument.Open(pdf);
        var image = document.GetPage(pageNumber).GetImages().OrderByDescending(i => i.BoundingBox.Width * i.BoundingBox.Height).FirstOrDefault();
        return image is null || image.BoundingBox.Width <= 0 ? null : (int)Math.Round(image.WidthInSamples / (image.BoundingBox.Width / 72));
    }
}
