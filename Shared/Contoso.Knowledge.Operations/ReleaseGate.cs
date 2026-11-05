using Contoso.Knowledge.Evaluation;

namespace Contoso.Knowledge.Operations;

/// <summary>
/// When a candidate may replace the live version: the paired bootstrap's lower bound for the change in the metric must
/// stay above <see cref="WorstDrop"/>, and no must-pass question (the signature questions, the restricted ones) may
/// lose what it had.
/// </summary>
public sealed record GatePolicy(double WorstDrop, IReadOnlySet<string> MustPass);

public sealed record GateResult(bool Passed, Difference Change, IReadOnlyList<string> Regressed, IReadOnlyList<string> Improved, IReadOnlyList<string> MustPassLost)
{
    public string Reason => !Passed && MustPassLost.Count > 0 ? $"must-pass questions lost: {string.Join(", ", MustPassLost)}"
        : !Passed ? $"lower bound {Change.Low.ToString("+0.000;-0.000", System.Globalization.CultureInfo.InvariantCulture)} below the allowed drop" : "passed";
}

public static class ReleaseGate
{
    /// <summary>Compares two runs over the same questions, question by question, so a regression can't hide in an improved average.</summary>
    public static GateResult Evaluate(IReadOnlyList<(string Id, double Live, double Candidate)> scores, GatePolicy policy)
    {
        var change = Bootstrap.Paired([.. scores.Select(s => s.Live)], [.. scores.Select(s => s.Candidate)]);
        var regressed = scores.Where(s => s.Candidate < s.Live).Select(s => s.Id).ToList();
        var improved = scores.Where(s => s.Candidate > s.Live).Select(s => s.Id).ToList();
        var lost = regressed.Where(policy.MustPass.Contains).ToList();
        return new GateResult(change.Low >= -policy.WorstDrop && lost.Count == 0, change, regressed, improved, lost);
    }
}
