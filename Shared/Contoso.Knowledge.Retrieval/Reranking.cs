using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Retrieval;

/// <summary>Reorders a question's candidates and keeps the best few (Chapter 4's re-ranker port).</summary>
public interface IReranker
{
    string Name { get; }

    Task<IReadOnlyList<Hit>> RerankAsync(string question, IReadOnlyList<Hit> candidates, int top, CancellationToken cancellationToken = default);
}

/// <summary>
/// A cross-encoder on ONNX Runtime: the question and a passage go through the model together as one sequence,
/// "[CLS] question [SEP] passage [SEP]", and a classification head returns one relevance logit for the pair.
/// </summary>
public sealed class OnnxCrossEncoder : IDisposable
{
    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;

    public OnnxCrossEncoder(string name, string modelPath, string vocabPath, int maxTokens = 512, int intraOpThreads = 0, bool spinning = true)
    {
        (Name, MaxTokens) = (name, maxTokens);
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true, RemoveNonSpacingMarks = true });
        using var options = new SessionOptions
        {
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        // Idle intra-op threads spin while they wait for work; with a second session in the process they steal its cores.
        if (!spinning) options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _session = new InferenceSession(modelPath, options);
        Score("warm-up", ["warm-up"]);
    }

    public string Name { get; }

    /// <summary>The most tokens a pair may have; the passage is cut to fit, never the question.</summary>
    public int MaxTokens { get; }

    /// <summary>One logit per passage: higher means more relevant to the question.</summary>
    public float[] Score(string question, IReadOnlyList<string> passages, int batchSize = 16)
    {
        var questionIds = _tokenizer.EncodeToIds(question, addSpecialTokens: false);
        var room = Math.Max(16, MaxTokens - questionIds.Count - 3);
        var pairs = passages.Select(p =>
        {
            var passageIds = _tokenizer.EncodeToIds(p, room, addSpecialTokens: false, out _, out _);
            var ids = _tokenizer.BuildInputsWithSpecialTokens(questionIds, passageIds);
            return (Ids: ids, Second: passageIds.Count + 1);   // the passage and its [SEP] are segment 1
        }).ToList();

        var scores = new float[passages.Count];
        // Batches of similar length waste less on padding (Chapter 10).
        foreach (var batch in Enumerable.Range(0, pairs.Count).OrderBy(i => pairs[i].Ids.Count).Chunk(batchSize))
        {
            var logits = Run([.. batch.Select(i => pairs[i])]);
            for (var j = 0; j < batch.Length; j++) scores[batch[j]] = logits[j];
        }

        return scores;
    }

    private float[] Run(IReadOnlyList<(IReadOnlyList<int> Ids, int Second)> pairs)
    {
        var (batch, length) = (pairs.Count, pairs.Max(p => p.Ids.Count));
        var ids = new long[batch * length];
        var mask = new long[batch * length];
        var types = new long[batch * length];
        for (var b = 0; b < batch; b++)
        {
            var (pair, second) = (pairs[b].Ids, pairs[b].Second);
            for (var t = 0; t < pair.Count; t++)
            {
                ids[b * length + t] = pair[t];
                mask[b * length + t] = 1;
                types[b * length + t] = t >= pair.Count - second ? 1 : 0;
            }
        }

        long[] shape = [batch, length];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var typesValue = OrtValue.CreateTensorValueFromMemory(types, shape);
        var inputs = new Dictionary<string, OrtValue> { ["input_ids"] = idsValue, ["attention_mask"] = maskValue, ["token_type_ids"] = typesValue };
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, inputs, [_session.OutputNames[0]]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();   // logits: [batch, 1]
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Re-ranks retrieved parents with a cross-encoder. Each candidate is scored on the text the model will read, its
/// breadcrumb and its parent's span, and the hits come back in the cross-encoder's order with its logits as scores.
/// </summary>
public sealed class OnnxCrossEncoderReranker(OnnxCrossEncoder model, Func<IndexedChunk, string>? evidence = null) : IReranker
{
    public string Name => model.Name;

    public Task<IReadOnlyList<Hit>> RerankAsync(string question, IReadOnlyList<Hit> candidates, int top, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0) return Task.FromResult<IReadOnlyList<Hit>>([]);
        var scores = model.Score(question, [.. candidates.Select(h => (evidence ?? Evidence)(h.Chunk))]);
        IReadOnlyList<Hit> ranked = [.. candidates.Select((h, i) => h with { Score = scores[i], Source = Name })
            .OrderByDescending(h => h.Score).Take(top)];
        return Task.FromResult(ranked);
    }

    /// <summary>The breadcrumb and the parent's text: what the answer model will be given.</summary>
    public static string Evidence(IndexedChunk chunk)
    {
        var (_, start, end) = chunk.ReturnKey;
        var text = chunk.Document.Text[start..end];
        return chunk.Chunk.Header is { } header ? $"{header}\n{text}" : text;
    }
}

/// <summary>Retrieves candidates with one retriever and lets a re-ranker choose the top few.</summary>
public sealed class RerankingRetriever(string name, IRetriever inner, IReranker reranker, int candidates = 20) : IRetriever
{
    public string Name { get; } = name;

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var first = await inner.RetrieveAsync(request with { Top = candidates }, cancellationToken);
        var hits = await reranker.RerankAsync(request.Question, first.Hits, request.Top, cancellationToken);
        return new RetrievalResult(hits, first.Degraded);
    }
}

/// <summary>
/// Maximal Marginal Relevance over re-ranked candidates: each pick maximizes lambda times its relevance minus
/// (1 - lambda) times its greatest similarity to what's already picked. Relevance is the re-ranker's logit through a
/// sigmoid; similarity is the cosine of the candidates' embeddings. An optional cap limits parents per document, and
/// a candidate at least <paramref name="duplicate"/> similar to one already picked is a near-duplicate and is skipped.
/// </summary>
public sealed class MaximalMarginalRelevance(IReranker inner, Func<IndexedChunk, float[]> vector, double lambda = 0.7, int perDocument = int.MaxValue, double duplicate = 2.0) : IReranker
{
    public string Name => $"{inner.Name}, MMR lambda {lambda:0.0#}" + (perDocument == int.MaxValue ? "" : $", {perDocument} per document") + (duplicate > 1 ? "" : $", duplicates at {duplicate:0.00}");

    public async Task<IReadOnlyList<Hit>> RerankAsync(string question, IReadOnlyList<Hit> candidates, int top, CancellationToken cancellationToken = default)
    {
        var ranked = await inner.RerankAsync(question, candidates, candidates.Count, cancellationToken);
        var picked = new List<Hit>(top);
        var remaining = ranked.ToList();
        while (picked.Count < top && remaining.Count > 0)
        {
            Hit? best = null;
            var bestScore = double.NegativeInfinity;
            foreach (var hit in remaining)
            {
                if (picked.Count(p => p.Chunk.Chunk.Doc == hit.Chunk.Chunk.Doc) >= perDocument) continue;
                var redundancy = picked.Count == 0 ? 0 : picked.Max(p => TensorPrimitives.CosineSimilarity(vector(p.Chunk), vector(hit.Chunk)));
                if (redundancy >= duplicate) continue;
                var score = lambda * Calibration.Sigmoid(hit.Score) - (1 - lambda) * redundancy;
                if (score > bestScore) (best, bestScore) = (hit, score);
            }

            if (best is null) break;
            picked.Add(best);
            remaining.Remove(best);
        }

        return picked;
    }
}

/// <summary>
/// Platt scaling: a logistic curve fitted to labeled (logit, relevant) pairs, so that a score reads as the probability
/// that a candidate is relevant and a threshold means the same thing for every question.
/// </summary>
public sealed record Calibration(double A, double B)
{
    public double Probability(double logit) => Sigmoid(A * logit + B);

    public static double Sigmoid(double x) => 1 / (1 + Math.Exp(-x));

    /// <summary>Fits A and B by gradient descent on the log loss.</summary>
    public static Calibration Fit(IReadOnlyList<(double Logit, bool Relevant)> samples, int iterations = 5000, double rate = 0.05)
    {
        var (a, b) = (1.0, 0.0);
        for (var i = 0; i < iterations; i++)
        {
            var (ga, gb) = (0.0, 0.0);
            foreach (var (logit, relevant) in samples)
            {
                var error = Sigmoid(a * logit + b) - (relevant ? 1 : 0);
                (ga, gb) = (ga + error * logit, gb + error);
            }

            (a, b) = (a - rate * ga / samples.Count, b - rate * gb / samples.Count);
        }

        return new Calibration(a, b);
    }
}
