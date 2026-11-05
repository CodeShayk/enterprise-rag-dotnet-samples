using System.Numerics.Tensors;

namespace Ch02.Fundamentals;

/// <summary>Vector similarity written out by hand and with SIMD-accelerated TensorPrimitives.</summary>
public static class Similarity
{
    /// <summary>Cosine similarity, one element at a time: the formula, not the fast path.</summary>
    public static float ScalarCosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    /// <summary>The same result with SIMD instructions (AVX2 or AVX-512 on x64, Neon on Arm64).</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => TensorPrimitives.CosineSimilarity(a, b);

    /// <summary>Scales a vector to length 1, after which cosine similarity and the dot product are equal.</summary>
    public static void Normalize(Span<float> vector) => TensorPrimitives.Divide(vector, TensorPrimitives.Norm(vector), vector);

    public static float Euclidean(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => TensorPrimitives.Distance(a, b);

    /// <summary>A random unit vector, for benchmarks and tests that need no embedding model.</summary>
    public static float[] RandomUnitVector(Random random, int dimensions)
    {
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }

        Normalize(vector);
        return vector;
    }
}
