using System.Text.RegularExpressions;
using Contoso.Knowledge.Query;

namespace Contoso.Knowledge.Correction;

/// <summary>How much work a question gets, from Adaptive-RAG's three paths.</summary>
public enum Route
{
    /// <summary>Not a question about the documents: reply without retrieval.</summary>
    NoRetrieval,

    /// <summary>One retrieval, one generation and the cheap checks.</summary>
    SinglePass,

    /// <summary>The corrective loop: grading, correction, verification and regeneration within the budget.</summary>
    Iterative,
}

public sealed record RouteDecision(Route Route, string Reason);

/// <summary>
/// Routes by what's known before the expensive steps. Adaptive-RAG trains a classifier on the question; here the
/// question's form decides first (a greeting needs no retrieval, a compound question needs more than one search), then
/// the evidence grader's verdict on the first retrieval, which the re-ranker has already paid for.
/// </summary>
public static partial class AdaptiveRouter
{
    public static RouteDecision Before(string question) =>
        Conversational().IsMatch(question.Trim()) ? new(Route.NoRetrieval, "a greeting or thanks")
        : Compound.IsCompound(question) ? new(Route.Iterative, "a compound question")
        : new(Route.SinglePass, "one question; grade the evidence first");

    public static RouteDecision After(RouteDecision before, EvidenceVerdict verdict) =>
        before.Route is not Route.SinglePass ? before
        : verdict.Grade is EvidenceGrade.Correct ? new(Route.SinglePass, "evidence graded correct")
        : new(Route.Iterative, $"evidence graded {verdict.Grade.ToString().ToLowerInvariant()}");

    [GeneratedRegex(@"^(hi|hello|hey|thanks|thank you|good (morning|afternoon|evening))\b[\s!.,]*\w{0,12}[\s!.]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Conversational();
}
