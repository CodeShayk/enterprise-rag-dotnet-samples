using System.Globalization;
using OpenCvSharp;
using RapidOcrNet;
using SkiaSharp;
using TesseractOCR;
using TesseractOCR.Enums;

namespace Contoso.Knowledge.Ocr;

/// <summary>
/// Tesseract 5 through the TesseractOCR wrapper: an LSTM recognizer with its own page segmentation. Word boxes
/// and confidences come from its TSV output. Set <c>OMP_THREAD_LIMIT=1</c> before the first engine loads when
/// several engines run in parallel, or Tesseract's own threads compete with yours.
/// </summary>
public sealed class TesseractEngine : IOcrEngine
{
    private readonly Engine _engine;
    private readonly int _dpi;

    /// <param name="variant"><c>fast</c> (integer model) or <c>best</c> (float model) from <see cref="OcrModels"/>.</param>
    public TesseractEngine(string variant = "best", int dpi = 300)
    {
        _engine = new Engine(OcrModels.Tessdata(variant), Language.English, EngineMode.LstmOnly);
        _engine.SetVariable("user_defined_dpi", dpi);
        _dpi = dpi;
        Name = $"tesseract-{variant}";
    }

    public string Name { get; }

    public OcrPage Recognize(Mat image) => Read(image, PageSegMode.Auto);

    public OcrPage RecognizeRegion(Mat image) => Read(image, PageSegMode.SingleBlock);

    private OcrPage Read(Mat image, PageSegMode mode)
    {
        Cv2.ImEncode(".png", image, out var png);
        using var pix = TesseractOCR.Pix.Image.LoadFromMemory(png);
        using var page = _engine.Process(pix, mode);
        return new OcrPage(image.Width, image.Height, FromTsv(page.TsvText));
    }

    /// <summary>Tesseract's TSV: level, page, block, paragraph, line, word, left, top, width, height, confidence, text.</summary>
    internal static List<OcrLine> FromTsv(string tsv)
    {
        var words = new List<(string Key, OcrWord Word)>();
        foreach (var row in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = row.TrimEnd('\r').Split('\t');
            if (f.Length < 12 || f[0] != "5" || string.IsNullOrWhiteSpace(f[11])) continue;
            int I(int i) => int.Parse(f[i], CultureInfo.InvariantCulture);
            var box = new PixelBox(I(6), I(7), I(6) + I(8), I(7) + I(9));
            var confidence = Math.Clamp(double.Parse(f[10], CultureInfo.InvariantCulture) / 100, 0, 1);
            words.Add(($"{f[2]}.{f[3]}.{f[4]}", new OcrWord(f[11].Trim(), box, confidence)));
        }

        return [.. words.GroupBy(w => w.Key).Select(g => Line([.. g.Select(w => w.Word)]))];
    }

    internal static OcrLine Line(IReadOnlyList<OcrWord> words)
    {
        var chars = words.Sum(w => w.Text.Length);
        var confidence = chars == 0 ? 0 : words.Sum(w => w.Confidence * w.Text.Length) / chars;
        return new OcrLine(string.Join(' ', words.Select(w => w.Text)), PixelBox.Union(words.Select(w => w.Box)), confidence, words);
    }

    public void Dispose() => _engine.Dispose();
}

/// <summary>
/// PaddleOCR's PP-OCRv5 models on ONNX Runtime, through RapidOcrNet: a DBNet text detector, an orientation
/// classifier and a CRNN recognizer with per-character scores. The default options cap the image's longer side
/// at 1,024 pixels for detection, which suits photos; pass <paramref name="maxSide"/> to keep a page's detail.
/// </summary>
public sealed class PaddleEngine : IOcrEngine
{
    private readonly RapidOcr _ocr = new();
    private readonly RapidOcrOptions _options;

    public PaddleEngine(int threads = 1, int maxSide = 1024)
    {
        string Model(string file) => Path.Combine(OcrModels.PaddleFolder, file);
        var models = RapidOcrModelSet.PPOCRv5Latin with
        {
            DetModelPath = Model("ch_PP-OCRv5_mobile_det.onnx"),
            ClsModelPath = Model("ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            RecModelPath = Model("latin_PP-OCRv5_rec_mobile_infer.onnx"),
            KeysPath = Model("ppocrv5_latin_dict.txt"),
        };
        using var session = RapidOcr.GetDefaultSessionOptions(threads);
        _ocr.InitModels(models, session);
        _options = RapidOcrOptions.Default with { ReturnWordBox = true, ImgResize = maxSide, RecMaxDegreeOfParallelism = threads };
        Name = maxSide == RapidOcrOptions.Default.ImgResize ? "paddle-v5" : $"paddle-v5-{maxSide}";
    }

    public string Name { get; }

    public OcrPage Recognize(Mat image)
    {
        Cv2.ImEncode(".png", image, out var png);
        using var bitmap = SKBitmap.Decode(png);
        var result = _ocr.Detect(bitmap, _options);
        var lines = new List<OcrLine>();
        foreach (var block in result.TextBlocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            var lineBox = Box(block.BoxPoints);
            var scores = block.CharScores is { Length: > 0 } s ? s.Average() : 0;
            var words = block.WordResults is { Length: > 0 } results
                ? results.Where(w => !string.IsNullOrWhiteSpace(w.Text)).Select(w => new OcrWord(w.Text.Trim(), Box(w.BoxPoints), w.Score)).ToList()
                : [new OcrWord(block.Text.Trim(), lineBox, scores)];
            lines.Add(new OcrLine(block.Text.Trim(), lineBox, scores, words));
        }

        return new OcrPage(image.Width, image.Height, lines);
    }

    private static PixelBox Box(IEnumerable<SKPointI> points)
    {
        var p = points.ToList();
        return new PixelBox(p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));
    }

    public void Dispose() => _ocr.Dispose();
}
