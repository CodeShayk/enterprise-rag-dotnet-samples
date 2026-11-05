using System.Numerics.Tensors;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Vision;
using SkiaSharp;

namespace Ch18.Tests;

/// <summary>CLIP ViT-B/32 from sample-code/models; skipped when <c>python eng/get-models.py</c> hasn't fetched it.</summary>
public sealed class ClipTests
{
    private static readonly string Folder = Path.Combine(EmbeddingModels.Root, "clip-vit-base-patch32");

    [Fact]
    public void Text_is_tokenized_as_CLIP_tokenizes_it()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(Folder, "text_model_quantized.onnx")), "CLIP isn't in sample-code/models.");
        using var clip = new ClipModel(Folder);

        var ids = clip.Encode("a photo of a cat");

        // The reference tokenizer's IDs for this text; the rest of the 77 positions are end tokens.
        Assert.Equal([49406, 320, 1125, 539, 320, 2368, 49407], ids[..7]);
        Assert.Equal(77, ids.Length);
        Assert.All(ids[7..], id => Assert.Equal(49407, id));
    }

    [Fact]
    public void Text_and_images_land_in_one_space_of_unit_vectors()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(Folder, "vision_model_quantized.onnx")), "CLIP isn't in sample-code/models.");
        using var clip = new ClipModel(Folder);
        using var page = new SKBitmap(300, 400);
        page.Erase(SKColors.White);

        var text = clip.EmbedText("an exploded view of a pump");
        var image = clip.EmbedImage(page);

        Assert.Equal(ClipModel.Dimensions, text.Length);
        Assert.Equal(ClipModel.Dimensions, image.Length);
        Assert.Equal(1, TensorPrimitives.Norm(text), 3);
        Assert.Equal(1, TensorPrimitives.Norm(image), 3);
        Assert.InRange(ClipModel.Similarity(text, image), -1, 1);
    }
}
