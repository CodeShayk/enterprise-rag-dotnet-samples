using System.Numerics;
using System.Numerics.Tensors;

namespace Contoso.Knowledge.Embeddings;

/// <summary>
/// Three ways to store a vector in less memory, each measured in Chapter 10 against full-precision search:
/// Matryoshka truncation (fewer dimensions), scalar quantization (one byte per dimension) and binary quantization
/// (one bit per dimension). The compressed vectors find candidates; full-precision vectors can rescore them.
/// </summary>
public static class VectorCompression
{
    /// <summary>The first <paramref name="dimensions"/> values, re-normalized. Only Matryoshka-trained models keep their meaning this way.</summary>
    public static float[] Truncate(ReadOnlySpan<float> vector, int dimensions)
    {
        var truncated = vector[..dimensions].ToArray();
        TensorPrimitives.Divide(truncated, TensorPrimitives.Norm(truncated), truncated);
        return truncated;
    }

    /// <summary>Scalar quantization: each value becomes a signed byte, scaled so the largest magnitude maps to 127.</summary>
    public static (sbyte[] Codes, float Scale) ToInt8(ReadOnlySpan<float> vector)
    {
        var scale = MathF.Abs(TensorPrimitives.MaxMagnitude(vector)) / 127f;
        var codes = new sbyte[vector.Length];
        for (var i = 0; i < vector.Length; i++) codes[i] = (sbyte)MathF.Round(vector[i] / scale);
        return (codes, scale);
    }

    /// <summary>The dot product of two quantized vectors, in the units of the originals.</summary>
    public static float DotInt8((sbyte[] Codes, float Scale) a, (sbyte[] Codes, float Scale) b)
    {
        var sum = 0;
        for (var i = 0; i < a.Codes.Length; i++) sum += a.Codes[i] * b.Codes[i];
        return sum * a.Scale * b.Scale;
    }

    /// <summary>
    /// Scalar quantization with a range per dimension, learned from the collection: each value becomes one byte
    /// between that dimension's minimum and maximum. A query stays in float32 and is scored against the codes.
    /// </summary>
    public sealed class ScalarQuantizer
    {
        private readonly float[] _min;
        private readonly float[] _step;

        public ScalarQuantizer(IReadOnlyList<float[]> vectors)
        {
            var dimensions = vectors[0].Length;
            _min = [.. Enumerable.Range(0, dimensions).Select(d => vectors.Min(v => v[d]))];
            _step = [.. Enumerable.Range(0, dimensions).Select(d => (vectors.Max(v => v[d]) - _min[d]) / 255f)];
        }

        public byte[] Encode(ReadOnlySpan<float> vector)
        {
            var codes = new byte[vector.Length];
            for (var d = 0; d < vector.Length; d++) codes[d] = (byte)Math.Clamp(MathF.Round((vector[d] - _min[d]) / _step[d]), 0, 255);
            return codes;
        }

        /// <summary>The query's dot product with the vector the codes stand for.</summary>
        public float Score(ReadOnlySpan<float> query, byte[] codes)
        {
            var score = 0f;
            for (var d = 0; d < codes.Length; d++) score += query[d] * (_min[d] + codes[d] * _step[d]);
            return score;
        }
    }

    /// <summary>Binary quantization: one bit per dimension, set when the value is positive.</summary>
    public static ulong[] ToBits(ReadOnlySpan<float> vector)
    {
        var bits = new ulong[(vector.Length + 63) / 64];
        for (var i = 0; i < vector.Length; i++)
        {
            if (vector[i] > 0) bits[i / 64] |= 1UL << (i % 64);
        }

        return bits;
    }

    /// <summary>How many bits differ: the distance binary codes are compared by (smaller is closer).</summary>
    public static int Hamming(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        var distance = 0;
        for (var i = 0; i < a.Length; i++) distance += BitOperations.PopCount(a[i] ^ b[i]);
        return distance;
    }
}
