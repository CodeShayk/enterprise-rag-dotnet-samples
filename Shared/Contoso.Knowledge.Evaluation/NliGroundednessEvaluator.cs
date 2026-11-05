using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Contoso.Knowledge.Evaluation;

/// <summary>The evidence an answer was given, passed to evaluators as additional context.</summary>
public sealed class EvidenceContext(string evidence) : EvaluationContext("Evidence", evidence)
{
    public string Evidence { get; } = evidence;
}

/// <summary>
/// Groundedness without a language model, as a Microsoft.Extensions.AI.Evaluation evaluator: every sentence of the
/// response is checked against the evidence with the NLI model, and the metric is the share that the evidence entails.
/// It runs on the CPU in milliseconds and gives the same score every time.
/// </summary>
public sealed class NliGroundednessEvaluator(ClaimSupport support, double threshold = 0.5) : IEvaluator
{
    public const string MetricName = "NLI groundedness";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages, ChatResponse modelResponse, ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        var metric = new NumericMetric(MetricName);
        var evidence = additionalContext?.OfType<EvidenceContext>().FirstOrDefault()?.Evidence;
        var sentences = ClaimSupport.Split(modelResponse.Text);
        if (evidence is null || sentences.Count == 0)
        {
            metric.AddDiagnostics(EvaluationDiagnostic.Error(evidence is null ? "No EvidenceContext was given." : "The response has no sentences."));
            return ValueTask.FromResult(new EvaluationResult(metric));
        }

        var scores = support.Score([.. sentences.Select(s => (evidence, s))]);
        var entailed = scores.Count(s => s >= threshold);
        metric.Value = entailed / (double)sentences.Count;
        metric.Reason = $"{entailed} of {sentences.Count} sentences are entailed by the evidence (P(entailment) >= {threshold:0.00}).";
        metric.Interpretation = new EvaluationMetricInterpretation(
            metric.Value >= 0.8 ? EvaluationRating.Good : metric.Value >= 0.5 ? EvaluationRating.Average : EvaluationRating.Poor,
            failed: metric.Value < 0.8);
        return ValueTask.FromResult(new EvaluationResult(metric));
    }
}
