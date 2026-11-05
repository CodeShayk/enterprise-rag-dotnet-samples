namespace Contoso.Knowledge.Evaluation;

/// <summary>A difference between two configurations with its 95% confidence interval.</summary>
public sealed record Difference(double Mean, double Low, double High)
{
    /// <summary>Whether the interval excludes zero, so the sign of the difference is unlikely to be chance.</summary>
    public bool Clear => Low > 0 || High < 0;
}

public static class Bootstrap
{
    /// <summary>
    /// The paired bootstrap: resample questions with replacement, keep each question's pair of scores
    /// together, and take the 2.5th and 97.5th percentiles of the mean difference.
    /// </summary>
    public static Difference Paired(IReadOnlyList<double> baseline, IReadOnlyList<double> candidate, int resamples = 10_000, int seed = 2027)
    {
        if (baseline.Count != candidate.Count || baseline.Count == 0)
        {
            throw new ArgumentException("Paired scores need the same, non-empty set of questions.");
        }

        var n = baseline.Count;
        var random = new Random(seed);
        var means = new double[resamples];
        for (var r = 0; r < resamples; r++)
        {
            double sum = 0;
            for (var i = 0; i < n; i++)
            {
                var q = random.Next(n);
                sum += candidate[q] - baseline[q];
            }

            means[r] = sum / n;
        }

        Array.Sort(means);
        var observed = Enumerable.Range(0, n).Average(i => candidate[i] - baseline[i]);
        return new Difference(observed, means[(int)(0.025 * resamples)], means[(int)(0.975 * resamples) - 1]);
    }
}

public static class Power
{
    /// <summary>
    /// Questions needed to detect a difference of <paramref name="delta"/> in a paired yes-or-no metric
    /// (McNemar's test, normal approximation, 5% significance, 80% power) when the two configurations
    /// disagree on a share <paramref name="discordant"/> of the questions.
    /// </summary>
    public static int PairedQuestions(double discordant, double delta, double zAlpha = 1.959964, double zBeta = 0.841621)
    {
        // A difference can't exceed the share of questions on which the two configurations disagree.
        if (delta <= 0 || delta >= discordant)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "The difference must be positive and smaller than the disagreement rate.");
        }

        return (int)Math.Ceiling(Math.Pow(zAlpha * Math.Sqrt(discordant) + zBeta * Math.Sqrt(discordant - delta * delta), 2) / (delta * delta));
    }
}

public static class Agreement
{
    /// <summary>Cohen's kappa: agreement between two annotators, corrected for the agreement chance alone would give.</summary>
    public static double CohensKappa(IReadOnlyList<bool> first, IReadOnlyList<bool> second)
    {
        var n = (double)first.Count;
        var observed = first.Zip(second).Count(p => p.First == p.Second) / n;
        var yes1 = first.Count(x => x) / n;
        var yes2 = second.Count(x => x) / n;
        var expected = yes1 * yes2 + (1 - yes1) * (1 - yes2);
        return expected == 1 ? 1 : (observed - expected) / (1 - expected);
    }
}
