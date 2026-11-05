using System.Diagnostics;
using System.Text.RegularExpressions;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Formats.Html;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace Ch15.Evaluation.Cli;

/// <summary>The experiments that call llama3.2: the local judge and its biases, and Microsoft.Extensions.AI.Evaluation.</summary>
public static partial class ModelCommands
{
    /// <summary>Two sentences that add words and no facts: the probe for verbosity bias.</summary>
    public const string Padding = " This answer is based on Contoso's controlled documents, which the responsible department reviews and approves. If anything is unclear, check the latest revision of the document before you start the work.";

    /// <summary>
    /// The local judge on the labeled sample: pointwise grades against the labels at three cuts, then pairwise
    /// verdicts in both orders, shipped against Chapter 3's answer (position bias) and each answer against itself
    /// padded (verbosity bias). Every call is recorded in runs/ch15 and replayed on reruns.
    /// </summary>
    public static async Task JudgeAsync(Lab lab)
    {
        var chat = Lab.Judge();
        var judge = new LocalJudge(chat);
        var (shipped, chapter3) = (Lab.Answers("compressed", "dev"), Lab.Answers("chapter3", "dev"));
        var labels = Lab.AnswerLabels().ToDictionary(l => l.Id, l => l.Correct);
        var sample = lab.Sample();

        var grades = new List<(string Id, JudgeVerdict? Verdict)>();
        foreach (var q in sample) grades.Add((q.Id, await judge.GradeAsync(q.Question, lab.References(q), Plain(shipped[q.Id].Text))));
        var graded = grades.Where(g => g.Verdict is not null).ToList();
        var truth = graded.Select(g => labels[g.Id]).ToList();
        Console.WriteLine($"Pointwise: llama3.2 ({LocalJudge.Version}) grades the {sample.Count} labeled shipped answers; {grades.Count - graded.Count} verdicts unusable");
        Console.WriteLine($"Scores: {string.Join(", ", Enumerable.Range(1, 5).Select(s => $"{s}: {graded.Count(g => g.Verdict!.Score == s)}"))}");
        Console.WriteLine();
        Console.WriteLine($"{"Correct when",-14} {"Judge yes",9} {"Both yes",9} {"Judge only",11} {"Labels only",12} {"Both no",8} {"Agree",6} {"Kappa",6}");
        foreach (var cut in new[] { 3, 4, 5 })
        {
            var judged = graded.Select(g => g.Verdict!.Score >= cut).ToList();
            var pairs = truth.Zip(judged).ToList();
            Console.WriteLine($"{(cut == 5 ? "score = 5" : $"score >= {cut}"),-14} {judged.Count(j => j),9} {pairs.Count(p => p.First && p.Second),9} {pairs.Count(p => !p.First && p.Second),11} " +
                $"{pairs.Count(p => p.First && !p.Second),12} {pairs.Count(p => !p.First && !p.Second),8} {Commands.Agree(truth, judged),6:0.00} {Agreement.CohensKappa(truth, judged),6:0.00}");
        }

        foreach (var (id, verdict) in graded.Where(g => labels[g.Id] != g.Verdict!.Score >= 4))
        {
            Console.WriteLine($"  {id,-8} labeled {(labels[id] ? "correct" : "wrong"),-7} judge {verdict!.Score}: {Commands.Shorten(verdict.Reason, 110)}");
        }

        Console.WriteLine();
        await PairsAsync(lab, judge, sample, "Shipped against Chapter 3's answer", q => (Plain(shipped[q.Id].Text), Plain(chapter3[q.Id].Text)), "shipped", "Chapter 3");
        await PairsAsync(lab, judge, sample, "Each answer against itself padded", q => (Plain(shipped[q.Id].Text), Plain(shipped[q.Id].Text) + Padding), "original", "padded");

        var calls = Lab.RecordedCalls("judge-recordings.jsonl");
        if (calls.Count > 0)
        {
            var ms = calls.Select(c => c.TotalMs).Order().ToList();
            Console.WriteLine($"{calls.Count} judge calls ({chat.Replayed} replayed): {ms[ms.Count / 2] / 1000:0.0} s p50, {calls.Average(c => c.InputTokens):0} prompt tokens on average, {ms.Sum() / 60000:0} min in all");
        }
    }

    /// <summary>
    /// One pairwise experiment: every question judged with the two answers in both orders. A consistent verdict names
    /// the same answer (or a tie) both times; an inconsistent one that follows the position shows position bias.
    /// </summary>
    private static async Task PairsAsync(Lab lab, LocalJudge judge, IReadOnlyList<GoldenQuestion> sample, string title,
        Func<GoldenQuestion, (string X, string Y)> answers, string x, string y)
    {
        var outcomes = new List<string>();
        foreach (var q in sample)
        {
            var (first, second) = answers(q);
            var references = lab.References(q);
            var forward = (await judge.CompareAsync(q.Question, references, first, second))?.Better;
            var backward = (await judge.CompareAsync(q.Question, references, second, first))?.Better;
            string Winner(PairChoice? verdict, bool swapped) => verdict switch { PairChoice.A => swapped ? y : x, PairChoice.B => swapped ? x : y, PairChoice.Tie => "tie", _ => "unusable" };
            var (w1, w2) = (Winner(forward, false), Winner(backward, true));
            outcomes.Add(w1 == "unusable" || w2 == "unusable" ? "unusable"
                : w1 == w2 ? $"{w1} both times"
                : forward == backward && forward is PairChoice.A or PairChoice.B ? $"answer {(forward == PairChoice.A ? "shown first" : "shown second")} both times"
                : "split, once a tie");
        }

        Console.WriteLine($"{title}, both orders ({sample.Count} questions):");
        foreach (var group in outcomes.GroupBy(o => o).OrderByDescending(g => g.Count())) Console.WriteLine($"  {group.Count(),3}  {group.Key}");
        Console.WriteLine();
    }

    /// <summary>
    /// Microsoft.Extensions.AI.Evaluation on the labeled sample: the built-in groundedness and completeness evaluators
    /// with llama3.2, and the NLI groundedness evaluator, run twice through disk-based reporting with response caching.
    /// The second pass is served from the cache. Writes an HTML report and compares each metric with the labels.
    /// </summary>
    public static async Task MeaiAsync(Lab lab, int count, double threshold)
    {
        using var model = Lab.Nli(Lab.SupportModel);
        IEvaluator[] evaluators = [new GroundednessEvaluator(), new CompletenessEvaluator(), new NliGroundednessEvaluator(new ClaimSupport(model), threshold)];
        var shipped = Lab.Answers("compressed", "dev");
        var sample = lab.Sample().Take(count).ToList();
        var storage = Path.Combine(Lab.Runs, "ch15", "meai");
        var chat = new ChatConfiguration(Lab.Ollama());
        var metrics = new Dictionary<string, EvaluationResult>();
        for (var pass = 1; pass <= 2; pass++)
        {
            var reporting = DiskBasedReportingConfiguration.Create(storage, evaluators, chat, enableResponseCaching: true,
                timeToLiveForCacheEntries: TimeSpan.FromDays(3650), executionName: $"pass-{pass}");
            var clock = Stopwatch.StartNew();
            foreach (var q in sample)
            {
                var answer = shipped[q.Id];
                var evidence = string.Join("\n\n", answer.Evidence.Select(Grounding.Premise));
                await using var run = await reporting.CreateScenarioRunAsync(q.Id);
                metrics[q.Id] = await run.EvaluateAsync(new ChatMessage(ChatRole.User, q.Question), new ChatResponse(new ChatMessage(ChatRole.Assistant, Plain(answer.Text))),
                    [new GroundednessEvaluatorContext(evidence), new CompletenessEvaluatorContext(string.Join('\n', lab.References(q))), new EvidenceContext(evidence)]);
            }

            var results = new List<ScenarioRunResult>();
            await foreach (var result in reporting.ResultStore.ReadResultsAsync($"pass-{pass}")) results.Add(result);
            var turns = results.SelectMany(r => r.ChatDetails?.TurnDetails ?? []).ToList();
            var latency = turns.Where(t => t.CacheHit != true).Select(t => t.Latency.TotalSeconds).Order().ToList();
            Console.WriteLine($"Pass {pass}: {sample.Count} answers, {turns.Count} model calls, {turns.Count(t => t.CacheHit == true)} from the cache, {clock.Elapsed.TotalSeconds:0.0} s" +
                (latency.Count > 0 ? $" ({latency[latency.Count / 2]:0.0} s a call p50)" : ""));
            if (pass == 2) await new HtmlReportWriter(Path.Combine(storage, "report.html")).WriteReportAsync(results);
        }

        // Each metric against the labels: groundedness against "every claim supported", completeness against "correct".
        var supported = Lab.ClaimLabels().GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.All(l => l.Supported));
        var correct = Lab.AnswerLabels().ToDictionary(l => l.Id, l => l.Correct);
        Console.WriteLine();
        Console.WriteLine($"{"Metric",-22} {"Against",-24} {"Scored",6} {"Unusable",8} {"Agree",6} {"Kappa",6}");
        foreach (var (name, against, truth, pass) in new (string, string, IReadOnlyDictionary<string, bool>, Func<double, bool>)[]
        {
            (GroundednessEvaluator.GroundednessMetricName, "every claim supported", supported, v => v >= 4),
            (NliGroundednessEvaluator.MetricName, "every claim supported", supported, v => v >= 0.999),
            (CompletenessEvaluator.CompletenessMetricName, "correct", correct, v => v >= 4),
        })
        {
            var scored = sample.Where(q => truth.ContainsKey(q.Id) && metrics[q.Id].TryGet<NumericMetric>(name, out var m) && m.Value is not null)
                .Select(q => (Label: truth[q.Id], Metric: pass(metrics[q.Id].Get<NumericMetric>(name).Value!.Value))).ToList();
            var asked = sample.Count(q => truth.ContainsKey(q.Id));
            Console.WriteLine($"{name,-22} {against,-24} {scored.Count,6} {asked - scored.Count,8} {Commands.Agree([.. scored.Select(s => s.Label)], [.. scored.Select(s => s.Metric)]),6:0.00} " +
                $"{Agreement.CohensKappa([.. scored.Select(s => s.Label)], [.. scored.Select(s => s.Metric)]),6:0.00}");
        }

        Console.WriteLine();
        Console.WriteLine($"Report: {Path.GetRelativePath(Path.Combine(Lab.Runs, ".."), Path.Combine(storage, "report.html"))}");
    }

    /// <summary>An answer as a judge should read it: the [S1] and [1] markers removed, since a judge sees no sources.</summary>
    public static string Plain(string text) => Spaces().Replace(Markers().Replace(LeadIn().Replace(text, ""), ""), " ").Trim();

    [GeneratedRegex(@"\s*\[S?\d+\]")]
    private static partial Regex Markers();

    [GeneratedRegex(@"\b[Aa]ccording to\s+\[S?\d+\],?\s*")]
    private static partial Regex LeadIn();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}
