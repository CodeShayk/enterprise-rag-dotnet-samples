using System.Numerics.Tensors;
using BenchmarkDotNet.Attributes;
using Ch02.Fundamentals;

namespace Ch02.Benchmarks;

/// <summary>One similarity computation between two vectors, by hand and with TensorPrimitives.</summary>
[ShortRunJob]
public class SimilarityBenchmarks
{
    private float[] _a = [], _b = [];

    [Params(384, 768)]
    public int Dimensions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _a = Similarity.RandomUnitVector(random, Dimensions);
        _b = Similarity.RandomUnitVector(random, Dimensions);
    }

    [Benchmark(Baseline = true)]
    public float ScalarCosine() => Similarity.ScalarCosine(_a, _b);

    [Benchmark]
    public float SimdCosine() => TensorPrimitives.CosineSimilarity(_a, _b);

    [Benchmark]
    public float SimdDot() => TensorPrimitives.Dot(_a, _b);
}

/// <summary>Exact search: score every stored vector against the query and keep the best five.</summary>
[ShortRunJob]
public class SearchBenchmarks
{
    private const int Dimensions = 384;
    private float[] _vectors = [], _query = [];

    [Params(10_000, 100_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _vectors = new float[Count * Dimensions];
        for (var i = 0; i < Count; i++)
        {
            Similarity.RandomUnitVector(random, Dimensions).CopyTo(_vectors, i * Dimensions);
        }

        _query = Similarity.RandomUnitVector(random, Dimensions);
    }

    [Benchmark]
    public int TopFiveByDotProduct()
    {
        Span<float> best = stackalloc float[5];
        Span<int> ids = stackalloc int[5];
        best.Fill(float.MinValue);
        for (var i = 0; i < Count; i++)
        {
            var score = TensorPrimitives.Dot(_vectors.AsSpan(i * Dimensions, Dimensions), _query);
            if (score <= best[4])
            {
                continue;
            }

            var slot = 4;
            while (slot > 0 && score > best[slot - 1])
            {
                best[slot] = best[slot - 1];
                ids[slot] = ids[slot - 1];
                slot--;
            }

            best[slot] = score;
            ids[slot] = i;
        }

        return ids[0];
    }
}
