using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Graph;

/// <summary>
/// A name the NER model found: PER, ORG, LOC or MISC, its span in the tokenizer's normalized text and the model's
/// probability for its first token.
/// </summary>
public sealed record NamedEntity(string Label, string Text, int Start, int End, double Score);

/// <summary>
/// A general-purpose NER model (BERT-base fine-tuned on CoNLL-2003, cased) on ONNX Runtime. It knows people,
/// organizations and places, not part numbers; Contoso uses it to propose names the gazetteer doesn't have yet.
/// Words are labeled by their first word piece, and B-/I- tags are joined into spans.
/// </summary>
public sealed class OnnxNer : IDisposable
{
    private const int MaxTokens = 510;
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly string[] _labels;

    public OnnxNer(string folder)
    {
        _tokenizer = BertTokenizer.Create(Path.Combine(folder, "vocab.txt"), new BertOptions { LowerCaseBeforeTokenization = false, RemoveNonSpacingMarks = false });
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "config.json")));
        var labels = config.RootElement.GetProperty("id2label");
        _labels = [.. Enumerable.Range(0, labels.EnumerateObject().Count()).Select(i => labels.GetProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetString()!)];
        using var options = new SessionOptions { IntraOpNumThreads = 1 };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _session = new InferenceSession(Path.Combine(folder, "model_quantized.onnx"), options);
    }

    public IReadOnlyList<NamedEntity> Find(string text)
    {
        // Token offsets point into the normalized text (the tokenizer spaces out punctuation), so names are cut from it.
        var tokens = _tokenizer.EncodeToTokens(text, out var normalized, considerPreTokenization: true, considerNormalization: true)
            .Where(t => t.Id != _tokenizer.ClassificationTokenId && t.Id != _tokenizer.SeparatorTokenId).ToList();
        var found = new List<NamedEntity>();
        for (var from = 0; from < tokens.Count; from += MaxTokens) found.AddRange(Window(normalized ?? text, tokens.GetRange(from, Math.Min(MaxTokens, tokens.Count - from))));
        return found;
    }

    private IEnumerable<NamedEntity> Window(string text, List<EncodedToken> tokens)
    {
        var n = tokens.Count + 2;
        var ids = new long[n];
        ids[0] = _tokenizer.ClassificationTokenId;
        ids[^1] = _tokenizer.SeparatorTokenId;
        for (var i = 0; i < tokens.Count; i++) ids[i + 1] = tokens[i].Id;
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, n])) };
        if (_session.InputMetadata.ContainsKey("attention_mask")) inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), [1, n])));
        if (_session.InputMetadata.ContainsKey("token_type_ids")) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[n], [1, n])));
        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>();

        // One label per word: its first piece's argmax; pieces that start with ## extend the word.
        var words = new List<(string Label, double Score, int Start, int End)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var (start, end) = (tokens[i].Offset.Start.Value, tokens[i].Offset.End.Value);
            if (tokens[i].Value.StartsWith("##", StringComparison.Ordinal) && words.Count > 0)
            {
                words[^1] = words[^1] with { End = end };
                continue;
            }

            var scores = Enumerable.Range(0, _labels.Length).Select(k => (double)logits[0, i + 1, k]).ToArray();
            var best = Array.IndexOf(scores, scores.Max());
            var sum = scores.Sum(s => Math.Exp(s - scores[best]));
            words.Add((_labels[best], 1 / sum, start, end));
        }

        NamedEntity? open = null;
        foreach (var w in words)
        {
            var type = w.Label.Length > 2 ? w.Label[2..] : "";
            if (open is not null && w.Label == "I-" + open.Label)
            {
                open = open with { End = w.End, Text = text[open.Start..w.End] };
                continue;
            }

            if (open is not null) yield return open;
            open = type.Length > 0 ? new NamedEntity(type, text[w.Start..w.End], w.Start, w.End, w.Score) : null;
        }

        if (open is not null) yield return open;
    }

    public void Dispose() => _session.Dispose();
}
