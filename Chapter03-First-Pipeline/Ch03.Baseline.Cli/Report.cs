namespace Ch03.Baseline.Cli;

/// <summary>Prints the smoke-set results as a table and a summary.</summary>
public static class Report
{
    public static void PrintQuestions(IReadOnlyList<SmokeResult> results)
    {
        Console.WriteLine("ID   Kind          Asker   Expected            Rank  Retrieve  1st token    Total   In  Out  Answer");
        foreach (var r in results)
        {
            var expected = r.Question.Answerable ? r.Question.Expected[0] : "(abstain)";
            var rank = r.Question.Answerable ? (r.FirstRelevantRank?.ToString() ?? "miss") : (r.Abstained ? "ok" : "ans");
            Console.WriteLine(
                $"{r.Question.Id,-4} {r.Question.Kind,-13} {r.Question.Asker.Split('.')[0],-7} {expected,-19} {rank,4} " +
                $"{Ms(r.Retrieval),8} {Ms(r.Generation.TimeToFirstToken),10} {Ms(r.Generation.Total),8} " +
                $"{r.Generation.InputTokens,4} {r.Generation.OutputTokens,4}  {Shorten(r.Answer, 48)}");
        }

        Console.WriteLine();
    }

    public static void PrintSummary(IReadOnlyList<SmokeResult> results, int top)
    {
        var answerable = results.Where(r => r.Question.Answerable).ToList();
        var hits = answerable.Count(r => r.Hit);
        var mrr = answerable.Sum(r => r.FirstRelevantRank is { } rank ? 1.0 / rank : 0) / answerable.Count;
        var abstain = results.Where(r => !r.Question.Answerable).ToList();

        Console.WriteLine($"Hit rate@{top}:            {hits}/{answerable.Count} ({100.0 * hits / answerable.Count:F0}%), MRR {mrr:F2}");
        foreach (var kind in answerable.GroupBy(r => r.Question.Kind))
        {
            Console.WriteLine($"  {kind.Key,-22} {kind.Count(r => r.Hit)}/{kind.Count()}");
        }

        Console.WriteLine($"Abstained when it should: {abstain.Count(r => r.Abstained)}/{abstain.Count}");
        Console.WriteLine($"Answered when it could:   {answerable.Count(r => !r.Abstained)}/{answerable.Count}");
        Console.WriteLine($"Restricted chunks shown:  {results.Sum(r => r.RestrictedChunks)}");
        Console.WriteLine($"Retrieval latency:        p50 {Ms(P(results, r => r.Retrieval, 50))}, p95 {Ms(P(results, r => r.Retrieval, 95))}");
        Console.WriteLine($"Time to first token:      p50 {Ms(P(results, r => r.Generation.TimeToFirstToken, 50))}, p95 {Ms(P(results, r => r.Generation.TimeToFirstToken, 95))}");
        Console.WriteLine($"Generation total:         p50 {Ms(P(results, r => r.Generation.Total, 50))}, p95 {Ms(P(results, r => r.Generation.Total, 95))}");
        Console.WriteLine($"Input tokens per answer:  mean {results.Average(r => r.Generation.InputTokens):F0}, max {results.Max(r => r.Generation.InputTokens)}");
        Console.WriteLine($"Output tokens per answer: mean {results.Average(r => r.Generation.OutputTokens):F0}, max {results.Max(r => r.Generation.OutputTokens)}");
        Console.WriteLine($"Token counts:             {(results.All(r => r.Generation.TokensReported) ? "reported by the provider" : "counted with o200k_base (the provider reported none)")}");
    }

    // Nearest-rank percentile: good enough for 25 samples, and it never invents a value between two runs.
    private static TimeSpan P(IReadOnlyList<SmokeResult> results, Func<SmokeResult, TimeSpan> select, int percentile)
    {
        var sorted = results.Select(select).Order().ToList();
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static string Ms(TimeSpan time) =>
        time.TotalMilliseconds >= 1000 ? $"{time.TotalSeconds:F1} s" : $"{time.TotalMilliseconds:F1} ms";

    private static string Shorten(string text, int length)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= length ? flat : flat[..(length - 3)] + "...";
    }
}
