using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;

namespace Ch14.Generation.Cli;

/// <summary>
/// One answer scored against the golden set's labeled spans, with checks that need no judge: Chapter 15 adds the
/// judgments these can't make (whether each claim is entailed, whether the answer is complete).
/// </summary>
public sealed record AnswerScore(
    string Id, string Type, bool Answerable, AnswerStatus Status,
    double EvidenceSufficient, double Answered, double FactsCited, double CitationsOnTarget, double ClaimsVerified, double KeyValues,
    int EvidenceTokens, long PromptTokens, long OutputTokens, double FirstTokenMs, double TotalMs, int Problems)
{
    public bool Abstained => Status is AnswerStatus.NotFound or AnswerStatus.Clarify;
}

public static partial class Scoring
{
    public static AnswerScore Score(GoldenQuestion question, Answer answer, IReadOnlyList<LocatedSpan> spans, IReadOnlyDictionary<string, SourceDocument> documents)
    {
        var tokens = answer.Evidence.Sum(b => b.CountTokens(Lab.O200k));
        var t = answer.Timings;
        if (!question.Answerable)
        {
            return new AnswerScore(question.Id, question.Type, false, answer.Status, double.NaN, double.NaN, double.NaN, double.NaN,
                Verified(answer), double.NaN, tokens, t.PromptTokens, t.OutputTokens, t.FirstTokenMs, t.TotalMs, answer.Problems.Count);
        }

        var facts = spans.GroupBy(s => s.Fact).ToList();
        var cited = answer.Claims.Where(c => c.SourceExists).Select(c => answer.Evidence.First(b => b.Id == c.Source)).Distinct().ToList();
        var answered = answer.Status is AnswerStatus.Answered or AnswerStatus.Partial;
        var keyed = facts.Select(f => KeyValues(f, question.Question, documents)).Where(values => values.Count > 0).ToList();
        return new AnswerScore(question.Id, question.Type, true, answer.Status,
            EvidenceSufficient: facts.All(f => f.Any(s => answer.Evidence.Any(b => Covers(b, s)))) ? 1 : 0,
            Answered: answered ? 1 : 0,
            FactsCited: answered ? facts.Count(f => f.Any(s => cited.Any(b => Covers(b, s)))) / (double)facts.Count : 0,
            CitationsOnTarget: cited.Count == 0 ? double.NaN : cited.Count(b => spans.Any(s => Covers(b, s))) / (double)cited.Count,
            ClaimsVerified: Verified(answer),
            KeyValues: keyed.Count == 0 ? double.NaN : answered ? keyed.Count(values => values.Any(v => States(Squash(answer.Text), v))) / (double)keyed.Count : 0,
            tokens, t.PromptTokens, t.OutputTokens, t.FirstTokenMs, t.TotalMs, answer.Problems.Count);
    }

    /// <summary>
    /// The share of an answer's claims whose quote was found in the source they cite; NaN when nothing could be checked
    /// (no claims, or a prompt that asks for no quotes).
    /// </summary>
    private static double Verified(Answer answer)
    {
        if (answer.PromptVersion == "chapter3" || answer.Status is AnswerStatus.NotFound or AnswerStatus.Clarify) return double.NaN;
        var factual = answer.Claims.Where(c => c.Cited || answer.Status is AnswerStatus.Answered).ToList();
        return factual.Count == 0 ? double.NaN : factual.Count(c => c.Valid) / (double)factual.Count;
    }

    /// <summary>Whether an evidence block covers part of a labeled span.</summary>
    public static bool Covers(EvidenceBlock block, LocatedSpan span) =>
        block.DocumentId == span.Doc && block.Segments.Any(s => s.Start < span.End && span.Start < s.End);

    /// <summary>
    /// The values that state a fact: numbers with their units and identifiers in its labeled spans that the question
    /// doesn't already contain. An answer that states the fact contains at least one of them.
    /// </summary>
    public static IReadOnlyList<string> KeyValues(IEnumerable<LocatedSpan> fact, string question, IReadOnlyDictionary<string, SourceDocument> documents)
    {
        var asked = Squash(question);
        return [.. fact.SelectMany(s => Values().Matches(documents[s.Doc].Text[s.Start..s.End]).Select(m => Squash(m.Value)))
            .Where(v => v.Length > 1 && !asked.Contains(v, StringComparison.Ordinal))
            .Distinct()];
    }

    /// <summary>
    /// Whether a text states a value: the value itself, or for a number with a unit, the number however the unit is
    /// written ("32,000 h" and "32,000 operating hours").
    /// </summary>
    private static bool States(string text, string value) =>
        text.Contains(value, StringComparison.Ordinal) ||
        (Number().Match(value) is { Success: true } n && n.Length > 1 && n.Length < value.Length && text.Contains(n.Value, StringComparison.Ordinal));

    /// <summary>Lowercase, without spaces, middle dots, hyphens or thousands separators: "2,000 hours" and "2000 hours" agree.</summary>
    public static string Squash(string text) => Squashable().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex(@"\b[A-Z]{1,4}-?\d[\w-]*|\d[\d,.]*(?:\s?(?:N·m|Nm|bar|hours?|h|°C|mm|µm|months?|days?|weeks?|years?|kW|rpm|%|litres?|minutes?|seconds?))?")]
    private static partial Regex Values();

    [GeneratedRegex(@"[\s·,\-]")]
    private static partial Regex Squashable();

    [GeneratedRegex(@"^\d[\d.]*")]
    private static partial Regex Number();
}
