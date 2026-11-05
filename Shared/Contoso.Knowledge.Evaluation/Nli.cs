using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Evaluation;

/// <summary>
/// A natural-language-inference cross-encoder on ONNX Runtime. Premise and hypothesis go through the model together as
/// "[CLS] premise [SEP] hypothesis [SEP]", and a classification head returns one logit per label. The DeBERTa-v3 models
/// tokenize with SentencePiece and take no token type IDs; the label order comes from each model's config.
/// </summary>
public sealed class OnnxNliModel : IDisposable
{
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly int _entailment;

    public OnnxNliModel(string name, string folder, IReadOnlyList<string> labels, string modelFile = "model.onnx", int maxTokens = 512, int intraOpThreads = 0)
    {
        (Name, Labels, MaxTokens) = (name, labels, maxTokens);
        using (var spm = File.OpenRead(Path.Combine(folder, "spm.model")))
        {
            _tokenizer = SentencePieceTokenizer.Create(spm, addBeginningOfSentence: false, addEndOfSentence: false);
        }

        using var options = new SessionOptions
        {
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");   // it shares the CPU with other sessions (Chapter 12)
        _session = new InferenceSession(Path.Combine(folder, modelFile), options);
        _entailment = labels.ToList().IndexOf("entailment");
    }

    public string Name { get; }

    public IReadOnlyList<string> Labels { get; }

    public int MaxTokens { get; }

    /// <summary>The model's input for one pair. The premise is cut to fit; the hypothesis, the claim, never is.</summary>
    public IReadOnlyList<int> Encode(string premise, string hypothesis)
    {
        var claim = _tokenizer.EncodeToIds(hypothesis, addBeginningOfSentence: false, addEndOfSentence: false);
        var room = Math.Max(1, MaxTokens - claim.Count - 3);
        var evidence = _tokenizer.EncodeToIds(premise, addBeginningOfSentence: false, addEndOfSentence: false, room, out _, out _);
        return [_tokenizer.BeginningOfSentenceId, .. evidence, _tokenizer.EndOfSentenceId, .. claim, _tokenizer.EndOfSentenceId];
    }

    public int CountTokens(string text) => _tokenizer.CountTokens(text, addBeginningOfSentence: false, addEndOfSentence: false);

    /// <summary>The probability of entailment for each pair: the softmax of its logits.</summary>
    public double[] Entailment(IReadOnlyList<(string Premise, string Hypothesis)> pairs, int batchSize = 8) =>
        [.. Logits(pairs, batchSize).Select(l => Softmax(l)[_entailment])];

    public float[][] Logits(IReadOnlyList<(string Premise, string Hypothesis)> pairs, int batchSize = 8)
    {
        var encoded = pairs.Select(p => Encode(p.Premise, p.Hypothesis)).ToList();
        var logits = new float[pairs.Count][];
        foreach (var batch in Enumerable.Range(0, encoded.Count).OrderBy(i => encoded[i].Count).Chunk(batchSize))
        {
            var output = Run([.. batch.Select(i => encoded[i])]);
            for (var j = 0; j < batch.Length; j++) logits[batch[j]] = output[j];
        }

        return logits;
    }

    private float[][] Run(IReadOnlyList<IReadOnlyList<int>> batch)
    {
        var (size, length) = (batch.Count, batch.Max(b => b.Count));
        var ids = new long[size * length];
        var mask = new long[size * length];
        for (var b = 0; b < size; b++)
        {
            for (var t = 0; t < batch[b].Count; t++)
            {
                ids[b * length + t] = batch[b][t];
                mask[b * length + t] = 1;
            }
        }

        long[] shape = [size, length];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, new Dictionary<string, OrtValue> { ["input_ids"] = idsValue, ["attention_mask"] = maskValue }, [_session.OutputNames[0]]);
        var flat = outputs[0].GetTensorDataAsSpan<float>().ToArray();   // logits: [batch, labels]
        return [.. Enumerable.Range(0, size).Select(b => flat[(b * Labels.Count)..((b + 1) * Labels.Count)])];
    }

    public static double[] Softmax(float[] logits)
    {
        var max = logits.Max();
        var exp = logits.Select(l => Math.Exp(l - max)).ToArray();
        var sum = exp.Sum();
        return [.. exp.Select(e => e / sum)];
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Sentence-level support: a claim is checked against its whole evidence and against every window of three sentences,
/// and its support is the best probability of entailment. A long block is cut by the model's 512 tokens, and a fact in
/// its last lines would be lost without the windows; a window also spares the model text that only dilutes the premise.
/// </summary>
public sealed partial class ClaimSupport(OnnxNliModel model, int window = 3)
{
    public OnnxNliModel Model => model;

    public double[] Score(IReadOnlyList<(string Evidence, string Claim)> items)
    {
        var pairs = new List<(string, string)>();
        var owner = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            foreach (var premise in Premises(items[i].Evidence))
            {
                pairs.Add((premise, items[i].Claim));
                owner.Add(i);
            }
        }

        var entailment = model.Entailment(pairs);
        var best = new double[items.Count];
        for (var k = 0; k < pairs.Count; k++) best[owner[k]] = Math.Max(best[owner[k]], entailment[k]);
        return best;
    }

    /// <summary>The whole evidence, then windows of consecutive sentences, table rows and list items.</summary>
    public IEnumerable<string> Premises(string evidence)
    {
        yield return evidence;
        var sentences = Split(evidence);
        if (sentences.Count <= window) yield break;
        for (var i = 0; i + window <= sentences.Count; i++) yield return string.Join(' ', sentences.Skip(i).Take(window));
    }

    public static IReadOnlyList<string> Split(string text) =>
        [.. Boundary().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#') && !s.All(c => c is '|' or '-' or ' '))];

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""“(])|\n+")]
    private static partial Regex Boundary();
}
