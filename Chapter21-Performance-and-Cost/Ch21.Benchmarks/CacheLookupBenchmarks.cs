using BenchmarkDotNet.Attributes;
using Contoso.Knowledge.Performance;

namespace Ch21.Benchmarks;

/// <summary>
/// One lookup in a cache of answers: by exact key, which hashes the question and its evidence, or by meaning, which
/// compares the question's 384-dimension vector with every cached question in the asker's scope.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD", "Gen0", "Alloc Ratio")]
public class CacheLookupBenchmarks
{
    private const string Question = "What torque applies to the M12 flange bolts on P-201?";

    private const string Evidence = "CP200-MAN-2009:4848-5473:3f1c0a9e27b4;P201-ASSET:0-484:9b2e71c40d15;TRN-FS-2026-04:1302-1541:c07d5e8a3b61;SOP-FS-007:2210-2899:51e9a0b6f2c8";

    private readonly Dictionary<string, int> _exact = new(StringComparer.Ordinal);
    private SemanticCache<int> _semantic = null!;
    private float[] _query = null!;

    [Params(1_000, 10_000, 100_000)]
    public int Entries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(21);
        _semantic = new SemanticCache<int>(threshold: 0.97);
        for (var i = 0; i < Entries; i++)
        {
            _exact[CacheKeys.Hash("answer", "grounded-f04881ce", "llama3.2", $"question {i}", Evidence)] = i;
            _semantic.Add("fluid", $"question {i}", UnitVector(random), i);
        }

        _query = UnitVector(random);
    }

    [Benchmark(Baseline = true)]
    public int ExactKey() =>
        _exact.TryGetValue(CacheKeys.Hash("answer", "grounded-f04881ce", "llama3.2", CacheKeys.Normalize(Question), Evidence), out var value) ? value : -1;

    [Benchmark]
    public double SemanticScan() => _semantic.Find("fluid", Question, _query)?.Similarity ?? 0;

    private static float[] UnitVector(Random random)
    {
        var v = new float[384];
        for (var i = 0; i < v.Length; i++) v[i] = (float)(random.NextDouble() * 2 - 1);
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }
}
