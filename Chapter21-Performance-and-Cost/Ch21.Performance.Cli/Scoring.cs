using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;

namespace Ch21.Performance.Cli;

/// <summary>An answer's quality on Chapter 14's judge-free checks, so a faster configuration can show it isn't worse.</summary>
public sealed record Quality(bool Answerable, bool Answered, double FactsCited, double KeyValues, double Verified);

/// <summary>Chapter 14's scoring (facts cited, key values, claims verified), copied because chapters don't reference each other.</summary>
public static partial class Scoring
{
    public static Quality Score(GoldenQuestion question, Answer answer, IReadOnlyList<LocatedSpan> spans, IReadOnlyDictionary<string, SourceDocument> documents)
    {
        var answered = answer.Status is AnswerStatus.Answered or AnswerStatus.Partial;
        if (!question.Answerable) return new Quality(false, answered, double.NaN, double.NaN, Verified(answer));
        var facts = spans.GroupBy(s => s.Fact).ToList();
        var cited = answer.Claims.Where(c => c.SourceExists).Select(c => answer.Evidence.First(b => b.Id == c.Source)).Distinct().ToList();
        var keyed = facts.Select(f => KeyValues(f, question.Question, documents)).Where(values => values.Count > 0).ToList();
        return new Quality(true, answered,
            FactsCited: answered ? facts.Count(f => f.Any(s => cited.Any(b => Covers(b, s)))) / (double)facts.Count : 0,
            KeyValues: keyed.Count == 0 ? double.NaN : answered ? keyed.Count(values => values.Any(v => States(Squash(answer.Text), v))) / (double)keyed.Count : 0,
            Verified: Verified(answer));
    }

    private static double Verified(Answer answer)
    {
        if (answer.Status is AnswerStatus.NotFound or AnswerStatus.Clarify) return double.NaN;
        var factual = answer.Claims.Where(c => c.Cited || answer.Status is AnswerStatus.Answered).ToList();
        return factual.Count == 0 ? double.NaN : factual.Count(c => c.Valid) / (double)factual.Count;
    }

    private static bool Covers(EvidenceBlock block, LocatedSpan span) =>
        block.DocumentId == span.Doc && block.Segments.Any(s => s.Start < span.End && span.Start < s.End);

    private static IReadOnlyList<string> KeyValues(IEnumerable<LocatedSpan> fact, string question, IReadOnlyDictionary<string, SourceDocument> documents)
    {
        var asked = Squash(question);
        return [.. fact.SelectMany(s => Values().Matches(documents[s.Doc].Text[s.Start..s.End]).Select(m => Squash(m.Value)))
            .Where(v => v.Length > 1 && !asked.Contains(v, StringComparison.Ordinal))
            .Distinct()];
    }

    private static bool States(string text, string value) =>
        text.Contains(value, StringComparison.Ordinal) ||
        (Number().Match(value) is { Success: true } n && n.Length > 1 && n.Length < value.Length && text.Contains(n.Value, StringComparison.Ordinal));

    private static string Squash(string text) => Squashable().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex(@"\b[A-Z]{1,4}-?\d[\w-]*|\d[\d,.]*(?:\s?(?:N·m|Nm|bar|hours?|h|°C|mm|µm|months?|days?|weeks?|years?|kW|rpm|%|litres?|minutes?|seconds?))?")]
    private static partial Regex Values();

    [GeneratedRegex(@"[\s·,\-]")]
    private static partial Regex Squashable();

    [GeneratedRegex(@"^\d[\d.]*")]
    private static partial Regex Number();
}

/// <summary>Medians and percentiles the way every output in the chapter reports them.</summary>
public static class Stats
{
    public static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).Order().ToList();
        return sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    public static double Mean(IEnumerable<double> values)
    {
        var list = values.Where(v => !double.IsNaN(v)).ToList();
        return list.Count == 0 ? double.NaN : list.Average();
    }
}
