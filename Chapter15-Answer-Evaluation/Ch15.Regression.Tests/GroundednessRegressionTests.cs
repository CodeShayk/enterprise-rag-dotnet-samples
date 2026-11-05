using Ch15.Evaluation.Cli;
using Contoso.Knowledge.Evaluation;
using Gate = Contoso.Knowledge.Evaluation.Regression;

namespace Ch15.Regression.Tests;

/// <summary>
/// Groundedness as a test that can fail the build. Every answer's statements are checked against the sources they
/// cite with the NLI model the baseline was made with; each slice of the golden set is one test case, compared with
/// the baseline question by question. A slice fails only when its drop is clear and larger than the tolerance.
/// </summary>
public sealed class GroundednessRegressionTests
{
    private const double Tolerance = 0.05;

    private static readonly string BaselinePath = Path.Combine(AppContext.BaseDirectory, "baseline", "groundedness.dev.json");

    private static readonly Lazy<IReadOnlyList<SliceComparison>> Comparison = new(Compare);

    /// <summary>Every slice the baseline scored; unanswerable questions are declined, so groundedness doesn't apply.</summary>
    public static TheoryData<string> Slices =>
        [Gate.All, .. Grounding.Read(BaselinePath).Questions.Where(q => !double.IsNaN(q.Score)).Select(q => q.Slice).Distinct().Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Slices))]
    public void Groundedness_holds_within_tolerance(string slice)
    {
        var baseline = Grounding.Read(BaselinePath);
        Assert.SkipUnless(Lab.NliAvailable(baseline.Nli), $"Run `python eng/get-models.py` for the {baseline.Nli} NLI model first.");
        Assert.SkipUnless(File.Exists(AnswersPath), $"No answers at {AnswersPath}; run Chapter 14's `answer --setup compressed` first.");

        var result = Comparison.Value.SingleOrDefault(r => r.Slice == slice);

        Assert.True(result is not null, $"The candidate answered none of the '{slice}' questions the baseline answered.");
        Assert.False(result.Failed,
            $"Groundedness of '{slice}' fell from {result.Baseline:0.00} to {result.Candidate:0.00} ({Lab.Interval(result.Change)}) over {result.Questions} questions; the tolerance is {Tolerance:0.00}.");
    }

    private static string AnswersPath =>
        Environment.GetEnvironmentVariable("CONTOSO_ANSWERS") is { Length: > 0 } path ? path : Path.Combine(Lab.Runs, "ch14", "answers", "compressed-dev.jsonl");

    private static IReadOnlyList<SliceComparison> Compare()
    {
        var baseline = Grounding.Read(BaselinePath);
        using var model = Lab.Nli(baseline.Nli);
        var slices = baseline.Questions.ToDictionary(q => q.Id, q => q.Slice);
        var candidate = Grounding.Score(Lab.ReadAnswers(AnswersPath), id => slices.GetValueOrDefault(id, ""), new CitationScorer(new ClaimSupport(model), baseline.Threshold));
        return Gate.Compare(baseline.Questions, candidate, Tolerance);
    }
}
