using System.Diagnostics;
using System.Globalization;

namespace Contoso.Knowledge.Observability;

/// <summary>A document range a trace names: a hit's returned span or one segment of the evidence the model read.</summary>
public sealed record SpanRef(string Doc, int Start, int End)
{
    public static SpanRef Parse(string text)
    {
        var colon = text.LastIndexOf(':');
        var dash = text.IndexOf('-', colon);
        return new SpanRef(text[..colon], int.Parse(text[(colon + 1)..dash], CultureInfo.InvariantCulture), int.Parse(text[(dash + 1)..], CultureInfo.InvariantCulture));
    }

    public bool Overlaps(string doc, int start, int end) => Doc == doc && Start < end && start < End;
}

/// <summary>
/// One answer as its trace recorded it: everything a replay needs and nothing a policy forbids. The question's text
/// is present only under a capture mode that allows it; its hash always is.
/// </summary>
public sealed record AnswerRecord(
    string TraceId, string QuestionId, string QuestionSha, string? Question, string Asker, string Tenant,
    IReadOnlyList<SpanRef> Hits, IReadOnlyList<double> Scores, IReadOnlyList<SpanRef> Evidence, double BestProbability,
    string PromptVersion, string Model, string Status, double Confidence, IReadOnlyList<string> ClaimOutcomes,
    double TotalMs, long InputTokens, long OutputTokens)
{
    /// <summary>
    /// Reads a record from one trace's spans: the retrieval span's hits and scores, the answer span's evidence, claims
    /// and status, the assemble span's best probability, the model span's model and tokens.
    /// </summary>
    public static AnswerRecord FromTrace(IReadOnlyList<Activity> spans, string questionId, string questionSha, string? question, string asker, string tenant)
    {
        var answer = spans.Single(s => s.OperationName == "answer");
        var retrieve = spans.Where(s => s.OperationName.StartsWith("retrieve ", StringComparison.Ordinal)).OrderBy(s => s.StartTimeUtc).FirstOrDefault();
        var assemble = spans.FirstOrDefault(s => s.OperationName == "assemble");
        var model = spans.FirstOrDefault(s => s.GetTagItem("gen_ai.operation.name") as string == "chat");
        T? Tag<T>(Activity? a, string key) => a?.GetTagItem(key) is T value ? value : default;
        return new AnswerRecord(
            answer.TraceId.ToString(), questionId, questionSha, question, asker, tenant,
            [.. (Tag<string[]>(retrieve, "rag.hit.ids") ?? []).Select(SpanRef.Parse)],
            Tag<double[]>(retrieve, "rag.hit.scores") ?? [],
            [.. (Tag<string[]>(answer, "rag.evidence.segments") ?? []).Select(SpanRef.Parse)],
            Tag<double>(assemble, "rag.evidence.best_probability"),
            Tag<string>(answer, "rag.prompt.version") ?? "none",
            Tag<string>(model, "gen_ai.request.model") ?? "none",
            Tag<string>(answer, "rag.answer.status") ?? "unknown",
            Tag<double>(answer, "rag.answer.confidence"),
            Tag<string[]>(answer, "rag.claim.outcomes") ?? [],
            answer.GetTagItem("rag.answer.total_ms") is double total ? total : answer.Duration.TotalMilliseconds,
            Convert.ToInt64(model?.GetTagItem("gen_ai.usage.input_tokens") ?? 0L, CultureInfo.InvariantCulture),
            Convert.ToInt64(model?.GetTagItem("gen_ai.usage.output_tokens") ?? 0L, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Where an answer failed, from its trace and the labeled spans of the facts it needed, in the failure points of
/// Barnett and colleagues (2024): a fact missing from the hits is retrieval's (FP2), one in the hits but not in the
/// evidence the model read was lost in assembly or compression (FP3), and one the model read and still missed is
/// generation's (FP4 and FP7). A claim whose quote isn't in its source is a citation failure.
/// </summary>
public static class FailurePoints
{
    public static string ForAnswer(AnswerRecord record, IReadOnlyList<(string Doc, int Start, int End, string Fact)> labeled)
    {
        var facts = labeled.GroupBy(s => s.Fact).ToList();
        bool Holds(IReadOnlyList<SpanRef> ranges, IEnumerable<(string Doc, int Start, int End, string Fact)> fact) =>
            fact.Any(s => ranges.Any(r => r.Overlaps(s.Doc, s.Start, s.End)));
        if (facts.Any(f => !Holds(record.Hits, f))) return "retrieval";
        if (facts.Any(f => !Holds(record.Evidence, f))) return "assembly or compression";
        return "generation";
    }

    public static string ForClaim(AnswerRecord record, int claim) =>
        claim < record.ClaimOutcomes.Count && record.ClaimOutcomes[claim] is "quote-not-found" or "no-source" ? "citation" : "needs a support check";
}

/// <summary>Drift measures: how far this week's traffic, corpus and embeddings have moved from a baseline.</summary>
public static class Drift
{
    /// <summary>
    /// Population stability index between two distributions over the same categories. Below 0.1 is stable, 0.1 to 0.25
    /// worth a look, above 0.25 a shift; a category empty on one side counts as half an observation.
    /// </summary>
    public static double Psi(IReadOnlyDictionary<string, int> baseline, IReadOnlyDictionary<string, int> current)
    {
        var keys = baseline.Keys.Union(current.Keys).ToList();
        double Total(IReadOnlyDictionary<string, int> d) => keys.Sum(k => Math.Max(0.5, d.GetValueOrDefault(k)));
        var (b, c) = (Total(baseline), Total(current));
        return keys.Sum(k =>
        {
            var (p, q) = (Math.Max(0.5, baseline.GetValueOrDefault(k)) / b, Math.Max(0.5, current.GetValueOrDefault(k)) / c);
            return (q - p) * Math.Log(q / p);
        });
    }

    /// <summary>Each vector's cosine similarity to its nearest baseline vector (all unit length).</summary>
    public static IReadOnlyList<double> NearestSimilarity(IReadOnlyList<float[]> baseline, IReadOnlyList<float[]> current) =>
        [.. current.Select(v => baseline.Max(b => (double)System.Numerics.Tensors.TensorPrimitives.Dot(v, b)))];

    /// <summary>How fast an SLO's error budget burns: the observed error rate over the rate the target allows.</summary>
    public static double BurnRate(int bad, int total, double target) => total == 0 ? 0 : bad / (double)total / (1 - target);
}
