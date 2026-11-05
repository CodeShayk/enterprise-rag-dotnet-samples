using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Embeddings;

/// <summary>
/// A BERT-family embedding model in ONNX Runtime: WordPiece tokens cut at the model's limit, one session run per
/// batch, pooling and L2 normalization. One instance can be shared, because ONNX Runtime allows concurrent runs on
/// a session; <paramref name="intraOpThreads"/> sets how many threads each run may use (0 lets ONNX Runtime choose).
/// </summary>
public sealed class OnnxEmbeddingModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _tokenTypes;
    private int _truncated;

    public OnnxEmbeddingModel(EmbeddingModelSpec spec, int intraOpThreads = 0, bool spinning = true)
    {
        Spec = spec;
        // An uncased BERT vocabulary has no accented letters: lowercasing must also strip accents, as Hugging Face does.
        _tokenizer = BertTokenizer.Create(spec.VocabPath, new BertOptions { LowerCaseBeforeTokenization = spec.LowerCase, RemoveNonSpacingMarks = spec.LowerCase });
        using var options = new SessionOptions
        {
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        // Idle intra-op threads spin while they wait for work; turn that off when other sessions share the CPU (Chapter 12).
        if (!spinning) options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _session = new InferenceSession(spec.ModelPath, options);
        _tokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        Embed(["warm-up"]); // The first run allocates buffers and finishes optimizing the graph; keep it out of any measurement.
        _truncated = 0;
    }

    public EmbeddingModelSpec Spec { get; }

    public IReadOnlyList<string> InputNames => _session.InputNames;

    public IReadOnlyList<string> OutputNames => _session.OutputNames;

    /// <summary>Texts cut at the sequence limit so far: their tails are missing from their vectors.</summary>
    public int Truncated => _truncated;

    /// <summary>The token IDs the model sees for a text, [CLS] and [SEP] included, cut at the sequence limit.</summary>
    public IReadOnlyList<int> Tokenize(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, addSpecialTokens: true, considerPreTokenization: true, considerNormalization: true);
        if (ids.Count <= Spec.MaxTokens) return ids;

        // Too long: keep the first MaxTokens tokens, still ending with [SEP]. Nothing fails; the tail just isn't embedded.
        Interlocked.Increment(ref _truncated);
        return _tokenizer.EncodeToIds(text, Spec.MaxTokens, addSpecialTokens: true, out _, out _);
    }

    public float[][] Embed(IReadOnlyList<string> texts)
    {
        var tokens = texts.Select(Tokenize).ToList();
        var (batch, length, dimensions) = (tokens.Count, tokens.Max(t => t.Count), Spec.Dimensions);

        // Pad every sequence to the longest in the batch; the attention mask tells the model which tokens are real.
        var ids = new long[batch * length];
        var mask = new long[batch * length];
        for (var b = 0; b < batch; b++)
        {
            for (var i = 0; i < tokens[b].Count; i++)
            {
                ids[b * length + i] = tokens[b][i];
                mask[b * length + i] = 1;
            }
        }

        long[] shape = [batch, length];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var typesValue = OrtValue.CreateTensorValueFromMemory(new long[batch * length], shape);
        var inputs = new Dictionary<string, OrtValue> { ["input_ids"] = idsValue, ["attention_mask"] = maskValue };
        if (_tokenTypes) inputs["token_type_ids"] = typesValue;

        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, inputs, [_session.OutputNames[0]]);
        var hidden = outputs[0].GetTensorDataAsSpan<float>(); // last_hidden_state: [batch, length, dimensions]

        var vectors = new float[batch][];
        for (var b = 0; b < batch; b++)
        {
            var vector = vectors[b] = new float[dimensions];
            if (Spec.Pooling == Pooling.Cls)
            {
                hidden.Slice(b * length * dimensions, dimensions).CopyTo(vector);
            }
            else
            {
                for (var i = 0; i < tokens[b].Count; i++) TensorPrimitives.Add(vector, hidden.Slice((b * length + i) * dimensions, dimensions), vector);
                TensorPrimitives.Divide(vector, tokens[b].Count, vector);
            }

            TensorPrimitives.Divide(vector, TensorPrimitives.Norm(vector), vector);
        }

        return vectors;
    }

    public void Dispose() => _session.Dispose();
}
