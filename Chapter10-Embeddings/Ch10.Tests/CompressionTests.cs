using System.Numerics.Tensors;
using Contoso.Knowledge.Embeddings;

namespace Ch10.Tests;

public sealed class CompressionTests
{
    private static float[] Unit(params float[] values)
    {
        TensorPrimitives.Divide(values, TensorPrimitives.Norm(values), values);
        return values;
    }

    [Fact]
    public void Exact_search_returns_the_best_k_in_order_and_respects_the_filter()
    {
        var index = new ExactIndex([Unit(1, 0), Unit(0, 1), Unit(1, 1), Unit(-1, 0)]);

        var top = index.Search(Unit(1, 0.2f), 2);
        var filtered = index.Search(Unit(1, 0.2f), 2, i => i != 0);

        Assert.Equal([0, 2], top.Select(t => t.Index));
        Assert.Equal([2, 1], filtered.Select(t => t.Index));
    }

    [Fact]
    public void Int8_codes_keep_the_dot_product_and_bits_keep_the_signs()
    {
        var a = Unit(0.3f, -0.8f, 0.5f, 0.1f);
        var b = Unit(0.4f, -0.7f, 0.2f, -0.3f);

        var approximate = VectorCompression.DotInt8(VectorCompression.ToInt8(a), VectorCompression.ToInt8(b));

        Assert.Equal(TensorPrimitives.Dot(a, b), approximate, 0.01f);
        Assert.Equal(1, VectorCompression.Hamming(VectorCompression.ToBits(a), VectorCompression.ToBits(b)));
        Assert.Equal(1f, TensorPrimitives.Norm(VectorCompression.Truncate(a, 2)), 0.0001f);
    }
}
