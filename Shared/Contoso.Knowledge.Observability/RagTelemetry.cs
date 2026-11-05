using System.Diagnostics;
using System.Diagnostics.Metrics;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using OpenTelemetry;

namespace Contoso.Knowledge.Observability;

/// <summary>
/// The assistant's own telemetry beside the GenAI conventions that Microsoft.Extensions.AI emits for model calls: one
/// ActivitySource for pipeline spans and one Meter for quality signals, both named <c>Contoso.Knowledge</c>. Attribute
/// names use a <c>rag.</c> prefix; the OpenTelemetry semantic conventions have none for retrieval yet.
/// </summary>
public static class RagTelemetry
{
    public const string Name = "Contoso.Knowledge";

    /// <summary>The source name model calls are traced under (Microsoft.Extensions.AI's <c>UseOpenTelemetry</c>).</summary>
    public const string ModelsSource = "Contoso.Knowledge.Models";

    public static ActivitySource Source { get; } = new(Name);

    public static Meter Meter { get; } = new(Name, "1.0");

    public static Histogram<double> RetrievalDuration { get; } = Meter.CreateHistogram<double>("rag.retrieval.duration", "s", "Time to retrieve the hits for one search.");

    public static Histogram<double> AnswerDuration { get; } = Meter.CreateHistogram<double>("rag.answer.duration", "s", "Time from the question to the typed answer.");

    public static Counter<long> Answers { get; } = Meter.CreateCounter<long>("rag.answers", "{answer}", "Answers by status: answered, partial, not_found or clarify.");

    public static Counter<long> EmptyRetrievals { get; } = Meter.CreateCounter<long>("rag.retrieval.empty", "{search}", "Searches that returned nothing the asker may read.");

    public static Counter<long> WeakEvidence { get; } = Meter.CreateCounter<long>("rag.evidence.weak", "{answer}", "Answers whose best evidence block has a calibrated probability below 0.1.");

    public static Counter<long> Claims { get; } = Meter.CreateCounter<long>("rag.claims", "{claim}", "Claims by citation outcome: valid, invalid or uncited.");

    public static Counter<long> Checks { get; } = Meter.CreateCounter<long>("rag.checks", "{finding}", "Findings of Chapter 15's deterministic answer checks.");

    public static Counter<long> Feedback { get; } = Meter.CreateCounter<long>("rag.feedback", "{rating}", "Ratings users gave an answer: up or down.");

    /// <summary>Below this calibrated probability, Chapter 16's grader calls the evidence weak.</summary>
    public const double WeakBelow = 0.1;

    /// <summary>The quality signals of one answer, as metrics tagged by tenant: these feed the dashboards and the alerts.</summary>
    public static void RecordAnswer(Answer answer, string tenant, double seconds)
    {
        var status = answer.Status switch
        {
            AnswerStatus.Answered => "answered",
            AnswerStatus.Partial => "partial",
            AnswerStatus.NotFound => "not_found",
            _ => "clarify",
        };
        var byTenant = new TagList { { "contoso.tenant", tenant } };
        Answers.Add(1, new TagList { { "contoso.tenant", tenant }, { "rag.answer.status", status } });
        AnswerDuration.Record(seconds, byTenant);
        if (answer.Evidence.Count > 0 && answer.Evidence.Max(e => e.Probability) < WeakBelow) WeakEvidence.Add(1, byTenant);
        foreach (var c in answer.Claims)
        {
            Claims.Add(1, new TagList { { "contoso.tenant", tenant }, { "rag.claim.outcome", c.Valid ? "valid" : c.Cited ? "invalid" : "uncited" } });
        }

        foreach (var f in AnswerChecks.Run(answer.Text)) Checks.Add(1, new TagList { { "contoso.tenant", tenant }, { "rag.check", f.Check } });
    }
}

/// <summary>
/// A retriever that records a span per search: what was asked for, how many hits came back, their IDs (document and
/// span) and scores, and whether a source was degraded. IDs and scores aren't content; the question text is.
/// </summary>
public sealed class TracedRetriever(IRetriever inner) : IRetriever
{
    public string Name => inner.Name;

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        using var span = RagTelemetry.Source.StartActivity($"retrieve {inner.Name}");
        var clock = Stopwatch.StartNew();
        span?.SetTag("rag.retriever", inner.Name);
        span?.SetTag("rag.top", request.Top);
        var result = await inner.RetrieveAsync(request, cancellationToken);
        RagTelemetry.RetrievalDuration.Record(clock.Elapsed.TotalSeconds, new TagList { { "rag.retriever", inner.Name } });
        if (result.Hits.Count == 0) RagTelemetry.EmptyRetrievals.Add(1, new TagList { { "rag.retriever", inner.Name } });
        if (span is null) return result;
        span.SetTag("rag.hits", result.Hits.Count);
        span.SetTag("rag.hit.ids", result.Hits.Select(h => $"{h.Chunk.Document.Id}:{h.Chunk.Chunk.ReturnStart}-{h.Chunk.Chunk.ReturnEnd}").ToArray());
        span.SetTag("rag.hit.scores", result.Hits.Select(h => Math.Round(h.Score, 3)).ToArray());
        if (result.Degraded.Count > 0) span.SetTag("rag.degraded", result.Degraded.ToArray());
        return result;
    }
}

/// <summary>A re-ranker that records a span: the candidates it scored, how many it kept and their new scores.</summary>
public sealed class TracedReranker(IReranker inner) : IReranker
{
    public string Name => inner.Name;

    public async Task<IReadOnlyList<Hit>> RerankAsync(string question, IReadOnlyList<Hit> candidates, int top, CancellationToken cancellationToken = default)
    {
        using var span = RagTelemetry.Source.StartActivity($"rerank {inner.Name}");
        span?.SetTag("rag.candidates", candidates.Count);
        var kept = await inner.RerankAsync(question, candidates, top, cancellationToken);
        span?.SetTag("rag.hits", kept.Count);
        span?.SetTag("rag.hit.scores", kept.Select(h => Math.Round(h.Score, 3)).ToArray());
        return kept;
    }
}

/// <summary>
/// Copies the request's baggage (tenant, pseudonymous user) onto every span as it starts, so a trace can be filtered
/// by tenant in any backend without each component knowing who asked.
/// </summary>
public sealed class BaggageTagProcessor(params string[] keys) : BaseProcessor<Activity>
{
    public override void OnStart(Activity data)
    {
        foreach (var key in keys)
        {
            if (data.GetBaggageItem(key) is { } value) data.SetTag(key, value);
        }
    }
}
