using System.Numerics.Tensors;
using System.Text.Json;
using Contoso.Knowledge.Embeddings;

namespace Ch10.Tests;

/// <summary>A model that runs isn't proof the pipeline is right: compare it with an independent implementation.</summary>
public sealed class EmbeddingTests
{
    private static readonly OnnxEmbeddingModel Fp32 = new(EmbeddingModels.BgeSmall(int8: false));
    private static readonly OnnxEmbeddingModel Int8 = new(EmbeddingModels.BgeSmall(int8: true));

    public static TheoryData<int> References => new(Enumerable.Range(0, Load().Count));

    [Theory]
    [MemberData(nameof(References))]
    public void Tokens_and_vectors_match_the_python_reference(int index)
    {
        var reference = Load()[index];

        var tokens = Fp32.Tokenize(reference.Text);
        var vector = Fp32.Embed([reference.Text])[0];

        Assert.Equal(reference.Tokens, tokens);
        Assert.True(TensorPrimitives.CosineSimilarity(vector, reference.Vector) > 0.9999f);
    }

    [Fact]
    public void Int8_weights_stay_close_to_full_precision()
    {
        var texts = Load().Select(r => r.Text).ToList();

        var full = Fp32.Embed(texts);
        var quantized = Int8.Embed(texts);

        // Measured 0.959 to 0.980 on these texts: INT8 weights move every vector a little, so retrieval has to be
        // measured on the golden set, not assumed.
        var cosines = full.Zip(quantized, (a, b) => TensorPrimitives.CosineSimilarity(a, b)).ToList();
        Assert.All(cosines, c => Assert.True(c > 0.95f, $"cosine {c:F4}"));
    }

    [Fact]
    public void A_text_past_the_limit_is_cut_at_512_tokens_ending_with_sep()
    {
        var tokens = Fp32.Tokenize(string.Concat(Enumerable.Repeat("Inspect the seal faces for wear. ", 120)));

        Assert.Equal(512, tokens.Count);
        Assert.Equal(102, tokens[^1]);
    }

    private sealed record Reference(string Text, int[] Tokens, float[] Vector);

    private static List<Reference> Load()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "reference", "bge-small-en-v1.5.json"));
        using var json = JsonDocument.Parse(stream);
        return [.. json.RootElement.GetProperty("entries").EnumerateArray().Select(e => new Reference(
            e.GetProperty("text").GetString()!,
            [.. e.GetProperty("tokens").EnumerateArray().Select(t => t.GetInt32())],
            [.. e.GetProperty("vector").EnumerateArray().Select(v => v.GetSingle())]))];
    }
}
