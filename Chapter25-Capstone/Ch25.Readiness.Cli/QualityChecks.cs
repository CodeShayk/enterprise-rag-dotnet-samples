using Contoso.Corpus;
using System.Text.RegularExpressions;
using Contoso.Knowledge.Agents;
using Contoso.Knowledge.Evaluation;
using static System.FormattableString;

namespace Ch25.Readiness.Cli;

/// <summary>Does the assembled system answer as well as its parts measured, and does the golden set cover what users ask?</summary>
public static class QualityChecks
{
    public static IReadOnlyList<Check> All { get; } =
    [
        new("Q1", "quality", "Retrieval on golden set v2", "5, 9-12", "Knowledge Assistant team", RetrievalAsync),
        new("Q2", "quality", "Must-pass questions", "24", "Knowledge Assistant team", MustPassAsync),
        new("Q3", "quality", "Signature questions end to end", "14-16", "Knowledge Assistant team", SignatureAsync),
        new("Q4", "quality", "Questions that need records or a graph", "17, 19", "Knowledge Assistant team", RoutesAsync),
        new("Q5", "quality", "Golden set coverage", "5, 24", "Knowledge Assistant team", CoverageAsync),
    ];

    public static readonly string[] MustPass = ["G001", "G003", "G004", "G005", "G006", "G017"];

    /// <summary>Sufficiency through the service's own retriever: the live alias, permissions in the search, live ACLs after it.</summary>
    private static async Task<Finding> RetrievalAsync(ReviewContext c)
    {
        var live = await c.Service.Stack.LiveAsync();
        var means = new List<string>();
        var passed = true;
        foreach (var split in new[] { "dev", "test" })
        {
            var questions = c.Golden.Where(q => q.Split == split).ToList();
            var scores = await c.Parts.Corpus.ScoreAsync(c.Parts.Retriever, live.Chunks, questions);
            foreach (var (id, score) in scores) c.Sufficiency[id] = score;
            var mean = scores.Values.Average();
            passed &= mean >= 0.95;
            means.Add(Invariant($"{split} {mean:F2} ({scores.Count} answerable)"));
        }

        return new(passed ? CheckStatus.Pass : CheckStatus.Fail, $"{string.Join(", ", means)} on {live.Version}; the bar is 0.95");
    }

    private static Task<Finding> MustPassAsync(ReviewContext c)
    {
        var lost = MustPass.Where(id => c.Sufficiency.GetValueOrDefault(id) < 1).ToList();
        return Task.FromResult(new Finding(lost.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            lost.Count == 0 ? $"all {MustPass.Length} sufficient ({string.Join(", ", MustPass)})" : $"lost: {string.Join(", ", lost)}"));
    }

    /// <summary>Five signature questions answered with every claim verified and the labeled value stated; two declined.</summary>
    private static async Task<Finding> SignatureAsync(ReviewContext c)
    {
        var expected = new (string Id, string Value)[] { ("G001", "85"), ("G003", "seal-water pressure"), ("G004", "2,000"), ("G005", "PN-4471-C"), ("G006", "20 weeks") };
        var misses = new List<string>();
        foreach (var (id, value) in expected)
        {
            var q = c.Question(id);
            var reply = await c.AskAsync(q.Question, People.Find(q.Asker)!);
            var verified = reply.Claims.Count > 0 && reply.Claims.All(claim => claim.Verified);
            var stated = !c.LiveModel || reply.Text.Contains(value, StringComparison.OrdinalIgnoreCase); // the scripted model quotes a sentence, not the value
            if (reply.Status is not ("answered" or "partial") || !verified || !stated)
            {
                misses.Add($"{id} {reply.Status}, {reply.Claims.Count(claim => claim.Verified)} of {reply.Claims.Count} claims verified{(stated ? "" : $", no \"{value}\"")}");
            }
        }

        foreach (var id in new[] { "G007", "G008" })
        {
            var q = c.Question(id);
            var reply = await c.AskAsync(q.Question, People.Find(q.Asker)!);
            if (reply.Status != "not_found") misses.Add($"{id} {reply.Status} instead of an abstention");
        }

        return new(misses.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, misses.Count == 0
            ? $"G001, G003, G004, G005 and G006 answered, every claim verified{(c.LiveModel ? ", the labeled value stated" : " (values not checked offline)")}; G007 and G008 declined"
            : string.Join("; ", misses));
    }

    /// <summary>
    /// Signature questions 3 and 5, through Chapter 17's records step and Chapter 19's graph route: is the evidence there,
    /// and does the answer state the values? Nadia needs 2,000 hours or six months and no overdue work orders; Grace needs
    /// Fabrikam Seals, and no claim that there are no incidents, because the documents she can read don't say.
    /// </summary>
    private static async Task<Finding> RoutesAsync(ReviewContext c)
    {
        var nadia = await c.AskAsync("What is the service interval for pump P-201, and how many of its work orders were overdue last quarter?", People.Nadia);
        var grace = await c.AskAsync("Which suppliers provide parts affected by ECN-2291, and do any have open quality incidents?", People.Grace);
        if (!c.LiveModel) return new(CheckStatus.Warn, "the records step needs a live model and the values aren't checked offline; graph route on");

        var rows = nadia.Sources.Any(s => s.DocumentId == EvidenceLedger.RecordsDocument);
        var count = Regex.IsMatch(nadia.Text, @"(?:(?<![a-z0-9])(?:no|none|zero|0|not any)(?![a-z0-9]))[^.]{0,80}(?:overdue|late)|(?:overdue|late)[^.]{0,60}(?:(?<![a-z0-9])(?:none|zero|0)(?![a-z0-9]))", RegexOptions.IgnoreCase);
        var interval = Regex.IsMatch(nadia.Text, "2,?000", RegexOptions.IgnoreCase) && Regex.IsMatch(nadia.Text, "(?<![a-z0-9])(?:six months|6 months)(?![a-z0-9])", RegexOptions.IgnoreCase);
        var supplier = grace.Text.Contains("Fabrikam", StringComparison.OrdinalIgnoreCase);
        var invented = Regex.IsMatch(grace.Text, @"\b(?:no|none|not any)\b[^.]{0,50}\bincidents?\b", RegexOptions.IgnoreCase);
        var right = rows && count && interval && supplier && !invented;
        return new(right ? CheckStatus.Pass : CheckStatus.Warn,
            $"records step and graph route assembled. Signature question 3: {nadia.Status}, {(rows ? "rows in the evidence" : "no rows")}, count {(count ? "right" : "wrong")}, interval {(interval ? "right" : "wrong")}. " +
            $"Signature question 5: {grace.Status}, supplier {(supplier ? "named" : "missing")}{(invented ? ", claims no incidents without a source" : "")}");
    }

    private static Task<Finding> CoverageAsync(ReviewContext c)
    {
        var types = c.Golden.GroupBy(q => q.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        var figures = c.Golden.Count(q => q.Type == "figure" || (q.Tags?.Contains("figure") ?? false));
        var conversations = c.Golden.Count(q => q.Tags?.Contains("follow-up") ?? false);
        return Task.FromResult(new Finding(figures == 0 || conversations == 0 ? CheckStatus.Warn : CheckStatus.Pass,
            $"{c.Golden.Count} questions, {figures} about figures, {conversations} follow-ups: golden set v3 is due"));
    }
}
