namespace Contoso.Knowledge.Evaluation;

/// <summary>One question's score in a snapshot, with the slice of the golden set it belongs to.</summary>
public sealed record ScoredQuestion(string Id, string Slice, double Score);

/// <summary>One slice of a comparison: the baseline's and the candidate's means over the questions both scored.</summary>
public sealed record SliceComparison(string Slice, int Questions, double Baseline, double Candidate, Difference Change, bool Failed);

/// <summary>
/// A regression check between two snapshots of per-question scores. Questions are paired, so a slice's difference
/// comes with a paired bootstrap interval, and a slice fails only when its drop is both clear (the interval lies below
/// zero) and larger than the tolerance: noise from a nondeterministic model stays inside the interval, and a real
/// drop smaller than the tolerance is a decision, not a failure. A slice with fewer than <c>minQuestions</c> paired
/// questions is reported but never fails: a bootstrap over three questions can look certain and mean nothing.
/// </summary>
public static class Regression
{
    public const string All = "all";

    public static IReadOnlyList<SliceComparison> Compare(IReadOnlyList<ScoredQuestion> baseline, IReadOnlyList<ScoredQuestion> candidate, double tolerance, int minQuestions = 5)
    {
        var scored = candidate.Where(q => !double.IsNaN(q.Score)).ToDictionary(q => q.Id, q => q.Score);
        var paired = baseline.Where(q => !double.IsNaN(q.Score) && scored.ContainsKey(q.Id)).Select(q => (q.Slice, Before: q.Score, After: scored[q.Id])).ToList();
        return [.. new[] { All }.Concat(paired.Select(p => p.Slice).Distinct().Order(StringComparer.Ordinal)).Select(slice =>
        {
            var rows = paired.Where(p => slice == All || p.Slice == slice).ToList();
            var change = Bootstrap.Paired([.. rows.Select(r => r.Before)], [.. rows.Select(r => r.After)]);
            var failed = rows.Count >= minQuestions && change.High < 0 && change.Mean < -tolerance;
            return new SliceComparison(slice, rows.Count, rows.Average(r => r.Before), rows.Average(r => r.After), change, failed);
        })];
    }
}
