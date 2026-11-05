using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Retrieval;

/// <summary>
/// A SPLADE encoder on ONNX Runtime: the masked-language-model head scores every vocabulary term at every position,
/// and log(1 + ReLU(score)), max-pooled over the positions, becomes one weight per term. Most weights are zero, so
/// the result is a sparse vector over BERT's 30,522 WordPiece terms that an inverted index can serve.
/// </summary>
public sealed class SpladeEncoder : IDisposable
{
    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;

    public SpladeEncoder(string modelPath, string vocabPath, int maxTokens = 512, int intraOpThreads = 0)
    {
        MaxTokens = maxTokens;
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true, RemoveNonSpacingMarks = true });
        using var options = new SessionOptions
        {
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);
        VocabularySize = _session.OutputMetadata.Values.First().Dimensions[^1];
    }

    public int MaxTokens { get; }

    public int VocabularySize { get; }

    /// <summary>Tokens the model reads for a text, special tokens included, at most <see cref="MaxTokens"/>.</summary>
    public int CountTokens(string text) => _tokenizer.EncodeToIds(text, MaxTokens, addSpecialTokens: true, out _, out _).Count;

    /// <summary>The vocabulary term behind a dimension, for showing what a vector holds.</summary>
    public string Term(int id) => _tokenizer.Decode([id]) ?? $"#{id}";

    /// <summary>Sparse vectors for a batch: term ID to weight, zero weights left out.</summary>
    public IReadOnlyList<Dictionary<int, float>> Encode(IReadOnlyList<string> texts)
    {
        var tokens = texts.Select(t => _tokenizer.EncodeToIds(t, MaxTokens, addSpecialTokens: true, out _, out _)).ToList();
        var (batch, length) = (tokens.Count, tokens.Max(t => t.Count));
        var ids = new long[batch * length];
        var mask = new long[batch * length];
        for (var b = 0; b < batch; b++)
        {
            for (var t = 0; t < tokens[b].Count; t++)
            {
                ids[b * length + t] = tokens[b][t];
                mask[b * length + t] = 1;
            }
        }

        long[] shape = [batch, length];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var segmentsValue = OrtValue.CreateTensorValueFromMemory(new long[batch * length], shape);
        // This export names its inputs input_mask and segment_ids, not attention_mask and token_type_ids.
        var inputs = new Dictionary<string, OrtValue> { ["input_ids"] = idsValue, ["input_mask"] = maskValue, ["segment_ids"] = segmentsValue };
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, inputs, [_session.OutputNames[0]]);
        var logits = outputs[0].GetTensorDataAsSpan<float>(); // [batch, length, vocabulary]

        var vectors = new List<Dictionary<int, float>>(batch);
        var pooled = new float[VocabularySize];
        for (var b = 0; b < batch; b++)
        {
            Array.Clear(pooled);
            for (var t = 0; t < tokens[b].Count; t++)
            {
                var row = logits.Slice((b * length + t) * VocabularySize, VocabularySize);
                for (var v = 0; v < row.Length; v++)
                {
                    if (row[v] > pooled[v]) pooled[v] = row[v];   // max over positions; log1p and ReLU are monotonic
                }
            }

            var vector = new Dictionary<int, float>();
            for (var v = 0; v < pooled.Length; v++)
            {
                if (pooled[v] > 0) vector[v] = MathF.Log(1 + pooled[v]);
            }

            vectors.Add(vector);
        }

        return vectors;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Learned sparse retrieval: SPLADE vectors in an inverted index, scored by their dot product.</summary>
public sealed class SparseRetriever : IRetriever
{
    private readonly IReadOnlyList<IndexedChunk> _chunks;
    private readonly SpladeEncoder _encoder;
    private readonly Dictionary<int, List<(int Chunk, float Weight)>> _postings = [];

    public SparseRetriever(string name, IReadOnlyList<IndexedChunk> chunks, SpladeEncoder encoder, IReadOnlyList<Dictionary<int, float>> vectors)
    {
        (Name, _chunks, _encoder) = (name, chunks, encoder);
        for (var i = 0; i < vectors.Count; i++)
        {
            foreach (var (term, weight) in vectors[i])
            {
                if (!_postings.TryGetValue(term, out var postings)) _postings[term] = postings = [];
                postings.Add((i, weight));
            }
        }

        Postings = _postings.Values.Sum(p => p.Count);
    }

    public string Name { get; }

    /// <summary>Nonzero weights stored across all chunks: the index's size.</summary>
    public int Postings { get; }

    public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var query = _encoder.Encode([request.Question])[0];
        var scores = new Dictionary<int, double>();
        foreach (var (term, weight) in query)
        {
            if (!_postings.TryGetValue(term, out var postings)) continue;
            foreach (var (chunk, w) in postings) scores[chunk] = scores.GetValueOrDefault(chunk) + weight * w;
        }

        var ranked = scores.Where(s => _chunks[s.Key].Passes(request.Asker, request.Filter))
            .OrderByDescending(s => s.Value).ThenBy(s => s.Key)
            .Select(s => new Hit(_chunks[s.Key], s.Value, Name));
        return Task.FromResult(RetrievalResult.Of(Ranking.DistinctReturns(ranked, request.Top)));
    }
}
