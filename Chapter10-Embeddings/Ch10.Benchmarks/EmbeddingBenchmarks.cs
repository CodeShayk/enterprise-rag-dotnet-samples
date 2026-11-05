using BenchmarkDotNet.Attributes;
using Contoso.Knowledge.Embeddings;

namespace Ch10.Benchmarks;

/// <summary>
/// One call to the embedding model on one ONNX Runtime thread: a single chunk, or a batch of 32 chunks of the same
/// length (so no padding is wasted), with full-precision or INT8 weights. The chunk is about 150 tokens, the average
/// of Chapter 9's structure-aware chunks.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class EmbeddingBenchmarks
{
    private const string Chunk =
        "CP-200 Centrifugal Pump Operating and Maintenance Manual › 6 Assembly and disassembly › 6.3 Flange bolt torque\n" +
        "Table 6-2 gives the tightening torque for the CP-200 flange bolts. The values apply to bolts of property class 8.8, " +
        "lightly oiled, with the spiral-wound gaskets supplied with the pump. Tighten the M12 flange bolts to 85 N·m. Apply the " +
        "torque in a cross pattern in three passes: 30 N·m, 60 N·m, then 85 N·m. Re-check the torque after the first 24 hours " +
        "of operation. Don't reuse spiral-wound gaskets. A gasket that has been compressed once won't seal at the specified torque.";

    private OnnxEmbeddingModel _model = null!;
    private string[] _batch = [];

    [Params("fp32", "int8")]
    public string Weights { get; set; } = "";

    [Params(1, 32)]
    public int Batch { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _model = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: Weights == "int8"), intraOpThreads: 1);
        _batch = [.. Enumerable.Repeat(Chunk, Batch)];
    }

    [GlobalCleanup]
    public void Cleanup() => _model.Dispose();

    [Benchmark]
    public float[][] Embed() => _model.Embed(_batch);
}
