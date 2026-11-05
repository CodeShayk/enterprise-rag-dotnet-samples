using System.Diagnostics;

namespace Contoso.Knowledge.Generation;

/// <summary>
/// The generator's spans, under the source <c>Contoso.Knowledge.Generation</c>: answer, with assemble, compress and
/// validate inside it. They carry identifiers, counts, scores and versions, never the question or the evidence text;
/// an application that wants content adds it under its own policy (Chapter 20). Nothing is recorded unless a listener
/// such as the OpenTelemetry SDK subscribes to the source.
/// </summary>
public static class GenerationTelemetry
{
    public const string SourceName = "Contoso.Knowledge.Generation";

    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>What an answer span records about the answer: its kind, confidence, prompt, evidence and timings.</summary>
    public static void Tag(Activity? span, Answer answer)
    {
        if (span is null) return;
        span.SetTag("rag.answer.status", answer.Status switch
        {
            AnswerStatus.Answered => "answered",
            AnswerStatus.Partial => "partial",
            AnswerStatus.NotFound => "not_found",
            _ => "clarify",
        });
        span.SetTag("rag.answer.confidence", answer.Confidence);
        span.SetTag("rag.prompt.version", answer.PromptVersion);
        span.SetTag("rag.evidence.refs", answer.Evidence.Select(e => e.Reference).ToArray());
        span.SetTag("rag.evidence.segments", answer.Evidence.SelectMany(e => e.Segments.Select(s => $"{e.DocumentId}:{s.Start}-{s.End}")).ToArray());
        span.SetTag("rag.claims", answer.Claims.Count);
        span.SetTag("rag.claims.valid", answer.Claims.Count(c => c.Valid));
        span.SetTag("rag.claim.outcomes", answer.Claims.Select(c => c.Valid ? "valid" : !c.Cited ? "uncited" : !c.SourceExists ? "no-source" : "quote-not-found").ToArray());
        span.SetTag("rag.degraded", answer.Degraded.ToArray());
        span.SetTag("rag.time_to_first_token_ms", answer.Timings.FirstTokenMs);
        span.SetTag("rag.answer.total_ms", answer.Timings.TotalMs);
        if (answer.Problems.Count > 0) span.SetStatus(ActivityStatusCode.Error, string.Join("; ", answer.Problems));
    }
}
