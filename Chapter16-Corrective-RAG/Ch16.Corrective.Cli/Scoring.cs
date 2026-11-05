using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;

namespace Ch16.Corrective.Cli;

/// <summary>
/// One outcome scored: Chapter 14's checks against the golden set (answered, facts cited, key values stated),
/// Chapter 15's support (the share of the answer's claims that the block they cite entails) and what it cost.
/// </summary>
public sealed record QuestionScore(
    string Id, string Type, bool Answerable, string Route, bool Declined, double FactsCited, double KeyValues, double Supported,
    bool EvidenceSufficient, int ModelCalls, long PromptTokens, double Seconds, int Retries, string? Exhausted, bool Escalated);

public static partial class Scoring
{
    public static QuestionScore Score(Lab lab, GoldenQuestion question, Outcome outcome, CitationScorer scorer)
    {
        var (answer, result) = (outcome.Result.Answer, outcome.Result);
        var declined = answer.Status is AnswerStatus.NotFound or AnswerStatus.Clarify;
        var statements = declined ? [] : answer.Claims.Select(c => new Statement(c.Text, answer.Evidence.FirstOrDefault(b => b.Id == c.Source) is { } b ? [AnswerVerifier.Premise(b)] : [])).ToList();
        var supported = statements.Count == 0 ? double.NaN : scorer.Score(statements).Recall;
        var escalated = answer.Text.EndsWith(AbstentionPolicy.Escalated, StringComparison.Ordinal);
        if (!question.Answerable)
        {
            return new QuestionScore(question.Id, question.Type, false, outcome.Route, declined, double.NaN, double.NaN, supported, false,
                result.ModelCalls, result.PromptTokens, result.Seconds, result.Retries, result.Exhausted, escalated);
        }

        var spans = lab.Spans(question);
        var facts = spans.GroupBy(s => s.Fact).ToList();
        var cited = answer.Claims.Where(c => c.SourceExists).Select(c => answer.Evidence.First(b => b.Id == c.Source)).Distinct().ToList();
        var keyed = facts.Select(f => KeyValues(f, question.Question, lab.Documents)).Where(v => v.Count > 0).ToList();
        return new QuestionScore(question.Id, question.Type, true, outcome.Route, declined,
            FactsCited: declined ? 0 : facts.Count(f => f.Any(s => cited.Any(b => Covers(b, s)))) / (double)facts.Count,
            KeyValues: keyed.Count == 0 ? double.NaN : declined ? 0 : keyed.Count(values => values.Any(v => States(Squash(answer.Text), v))) / (double)keyed.Count,
            Supported: supported,
            EvidenceSufficient: facts.All(f => f.Any(s => answer.Evidence.Any(b => Covers(b, s)))),
            result.ModelCalls, result.PromptTokens, result.Seconds, result.Retries, result.Exhausted, escalated);
    }

    /// <summary>Whether an evidence block covers part of a labeled span.</summary>
    public static bool Covers(EvidenceBlock block, LocatedSpan span) =>
        block.DocumentId == span.Doc && block.Segments.Any(s => s.Start < span.End && span.Start < s.End);

    /// <summary>Whether evidence holds every labeled fact of an answerable question, as Chapter 14 counts it.</summary>
    public static bool Holds(Lab lab, GoldenQuestion question, IReadOnlyList<EvidenceBlock> evidence) =>
        question.Answerable && lab.Spans(question).GroupBy(s => s.Fact).All(f => f.Any(s => evidence.Any(b => Covers(b, s))));

    /// <summary>
    /// The values that state a fact (Chapter 14): numbers with their units and identifiers in its labeled spans that
    /// the question doesn't already contain. An answer that states the fact contains at least one of them.
    /// </summary>
    public static IReadOnlyList<string> KeyValues(IEnumerable<LocatedSpan> fact, string question, IReadOnlyDictionary<string, SourceDocument> documents)
    {
        var asked = Squash(question);
        return [.. fact.SelectMany(s => Values().Matches(documents[s.Doc].Text[s.Start..s.End]).Select(m => Squash(m.Value)))
            .Where(v => v.Length > 1 && !asked.Contains(v, StringComparison.Ordinal))
            .Distinct()];
    }

    private static bool States(string text, string value) =>
        text.Contains(value, StringComparison.Ordinal) ||
        (Number().Match(value) is { Success: true } n && n.Length > 1 && n.Length < value.Length && text.Contains(n.Value, StringComparison.Ordinal));

    public static string Squash(string text) => Squashable().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex(@"\b[A-Z]{1,4}-?\d[\w-]*|\d[\d,.]*(?:\s?(?:N·m|Nm|bar|hours?|h|°C|mm|µm|months?|days?|weeks?|years?|kW|rpm|%|litres?|minutes?|seconds?))?")]
    private static partial Regex Values();

    [GeneratedRegex(@"[\s·,\-]")]
    private static partial Regex Squashable();

    [GeneratedRegex(@"^\d[\d.]*")]
    private static partial Regex Number();
}
