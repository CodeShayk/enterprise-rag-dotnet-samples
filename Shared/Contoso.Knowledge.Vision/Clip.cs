using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SkiaSharp;

namespace Contoso.Knowledge.Vision;

/// <summary>
/// CLIP ViT-B/32 on ONNX Runtime: a text encoder and an image encoder trained so that a caption and its picture land
/// close together in one 512-dimensional space. Both encoders are the 8-bit exports in <c>models/clip-vit-base-patch32</c>.
/// </summary>
public sealed partial class ClipModel : IDisposable
{
    public const int ImageSize = 224, ContextLength = 77, Dimensions = 512;

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly InferenceSession _text;
    private readonly InferenceSession _vision;

    public ClipModel(string folder, int threads = 0)
    {
        var options = new SessionOptions { IntraOpNumThreads = threads };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _text = new InferenceSession(Path.Combine(folder, "text_model_quantized.onnx"), options);
        _vision = new InferenceSession(Path.Combine(folder, "vision_model_quantized.onnx"), options);
        Tokenizer = CreateTokenizer(folder);
        using var vocabulary = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "vocab.json")));
        (Start, End) = (vocabulary.RootElement.GetProperty("<|startoftext|>").GetInt32(), vocabulary.RootElement.GetProperty("<|endoftext|>").GetInt32());
    }

    public BpeTokenizer Tokenizer { get; }

    /// <summary>The start and end tokens' IDs (49406 and 49407), from the vocabulary.</summary>
    public int Start { get; }

    public int End { get; }

    /// <summary>
    /// CLIP's tokenizer: lowercase, its word pattern and byte-level BPE with an end-of-word suffix; <see cref="Encode"/>
    /// adds the start and end tokens.
    /// </summary>
    public static BpeTokenizer CreateTokenizer(string folder) => BpeTokenizer.Create(new BpeOptions(Path.Combine(folder, "vocab.json"), Path.Combine(folder, "merges.txt"))
    {
        ByteLevel = true,
        EndOfWordSuffix = "</w>",
        PreTokenizer = new RegexPreTokenizer(Words(), null),
        Normalizer = new LowerCaseNormalizer(),
    });

    /// <summary>The token IDs CLIP reads: start, the text's tokens (cut to fit), end, then end tokens as padding to 77.</summary>
    public long[] Encode(string text)
    {
        var ids = Tokenizer.EncodeToIds(Regex.Replace(text, @"\s+", " ").Trim());
        var tokens = new long[ContextLength];
        Array.Fill(tokens, (long)End);
        tokens[0] = Start;
        for (var i = 0; i < Math.Min(ids.Count, ContextLength - 2); i++) tokens[i + 1] = ids[i];
        return tokens;
    }

    public float[] EmbedText(string text)
    {
        var ids = Encode(text);
        var length = Array.IndexOf(ids, (long)End) + 1;
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ContextLength])) };
        if (_text.InputMetadata.ContainsKey("attention_mask"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(ids.Select((_, i) => i < length ? 1L : 0L).ToArray(), [1, ContextLength])));
        }

        using var results = _text.Run(inputs);
        return Normalize(results.First(r => r.Name == "text_embeds").AsEnumerable<float>().ToArray());
    }

    /// <summary>
    /// An image as CLIP sees it: the shorter side scaled to 224 pixels, the center 224 x 224 cropped, each channel
    /// scaled to 0..1 and standardized. A portrait page loses its top and bottom bands to the crop.
    /// </summary>
    public float[] EmbedImage(SKBitmap image)
    {
        var scale = ImageSize / (double)Math.Min(image.Width, image.Height);
        var (w, h) = (Math.Max(ImageSize, (int)Math.Round(image.Width * scale)), Math.Max(ImageSize, (int)Math.Round(image.Height * scale)));
        using var resized = image.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))!;
        var (left, top) = ((w - ImageSize) / 2, (h - ImageSize) / 2);
        var pixels = new float[3 * ImageSize * ImageSize];
        for (var y = 0; y < ImageSize; y++)
        {
            for (var x = 0; x < ImageSize; x++)
            {
                var c = resized.GetPixel(left + x, top + y);
                var i = y * ImageSize + x;
                pixels[i] = (c.Red / 255f - Mean[0]) / Std[0];
                pixels[ImageSize * ImageSize + i] = (c.Green / 255f - Mean[1]) / Std[1];
                pixels[2 * ImageSize * ImageSize + i] = (c.Blue / 255f - Mean[2]) / Std[2];
            }
        }

        using var results = _vision.Run([NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(pixels, [1, 3, ImageSize, ImageSize]))]);
        return Normalize(results.First(r => r.Name == "image_embeds").AsEnumerable<float>().ToArray());
    }

    public static double Similarity(float[] a, float[] b) => TensorPrimitives.Dot(a, b);

    private static float[] Normalize(float[] v)
    {
        var norm = TensorPrimitives.Norm(v);
        TensorPrimitives.Divide(v, norm, v);
        return v;
    }

    // CLIP's word pattern: contractions, runs of letters, single digits, runs of other symbols.
    [GeneratedRegex(@"'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Words();

    public void Dispose()
    {
        _text.Dispose();
        _vision.Dispose();
    }
}
