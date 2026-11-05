using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contoso.Corpus;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;

namespace Ch16.Corrective.Cli;

public static class Commands
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string OutcomesPath(string config, string split) => Path.Combine(Lab.Runs, "answers", $"{config}-{split}.jsonl");

    public static Dictionary<string, Outcome> Load(string config, string split) => !File.Exists(OutcomesPath(config, split)) ? [] :
        File.ReadLines(OutcomesPath(config, split)).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<Outcome>(l, Json)!).ToDictionary(o => o.Id);

    /// <summary>Answers the questions with one configuration and records each outcome; a rerun resumes where it stopped.</summary>
    public static async Task RunAsync(Lab lab, Pipelines pipelines, string config, IReadOnlyList<GoldenQuestion> questions)
    {
        var done = Load(config, lab.Split);
        Directory.CreateDirectory(Path.GetDirectoryName(OutcomesPath(config, lab.Split))!);
        var clock = Stopwatch.StartNew();
        foreach (var question in questions.Where(q => !done.ContainsKey(q.Id)))
        {
            var outcome = await pipelines.AnswerAsync(config, question, lab.Split);
            await File.AppendAllTextAsync(OutcomesPath(config, lab.Split), JsonSerializer.Serialize(outcome, Json) + "\n");
            Console.WriteLine($"{question.Id,-8} {outcome.Route,-28} {outcome.Result.Answer.Status,-9} calls {outcome.Result.ModelCalls} retries {outcome.Result.Retries} {outcome.Result.Seconds,6:0.0} s");
        }

        Console.WriteLine($"{config} on {lab.Split}: {questions.Count} questions, {pipelines.Chat.Recorded} model calls recorded, {pipelines.Chat.Replayed} replayed, {clock.Elapsed.TotalMinutes:0.0} min");
    }

    /// <summary>
    /// The evidence graders against the evidence's truth: does the shipped single pass's evidence hold every labeled
    /// fact? Unanswerable questions have no sufficient evidence. The CPU grader is read at several thresholds; the LLM
    /// grader (with --llm) asks llama3.2.
    /// </summary>
    public static async Task GradersAsync(Lab lab, Pipelines pipelines, bool llm)
    {
        var (assembler, compressor) = (pipelines.Assembler(), pipelines.Compressor());
        var rows = new List<(GoldenQuestion Q, AssembledContext Context, bool Sufficient)>();
        foreach (var q in lab.Questions)
        {
            var result = await pipelines.Retriever.RetrieveAsync(new RetrievalRequest(q.Question, People.Find(q.Asker)!, 4));
            var context = assembler.Assemble(result.Hits);
            if (!context.IsEmpty) context = compressor.Compress(q.Question, context);
            rows.Add((q, context, Scoring.Holds(lab, q, context.Blocks)));
        }

        var insufficient = rows.Count(r => !r.Sufficient);
        Console.WriteLine($"Golden set v2, {lab.Split}: {rows.Count} questions; the evidence holds every labeled fact for {rows.Count - insufficient}, " +
            $"not for {rows.Count(r => !r.Sufficient && r.Q.Answerable)} answerable and {rows.Count(r => !r.Q.Answerable)} unanswerable");
        Console.WriteLine();
        Console.WriteLine($"{"Grader",-44} {"Correct",8} {"Ambiguous",10} {"Incorrect",10} {"Caught",8} {"Needless",9} {"Kappa",6} {"Per question",13}");
        void Row(string name, IReadOnlyList<EvidenceVerdict> verdicts, string cost)
        {
            var truth = rows.Select(r => r.Sufficient).ToList();
            var correct = verdicts.Select(v => v.Grade is EvidenceGrade.Correct).ToList();
            var caught = rows.Zip(correct).Count(p => !p.First.Sufficient && !p.Second);
            var needless = rows.Zip(correct).Count(p => p.First.Sufficient && !p.Second);
            Console.WriteLine($"{name,-44} {verdicts.Count(v => v.Grade is EvidenceGrade.Correct),8} {verdicts.Count(v => v.Grade is EvidenceGrade.Ambiguous),10} " +
                $"{verdicts.Count(v => v.Grade is EvidenceGrade.Incorrect),10} {$"{caught}/{insufficient}",8} {$"{needless}/{rows.Count - insufficient}",9} {Agreement.CohensKappa(truth, correct),6:0.00} {cost,13}");
        }

        foreach (var from in new[] { 0.1, 0.2, 0.3, 0.5, 0.7 })
        {
            var grader = new CalibratedEvidenceGrader(from, Pipelines.IncorrectBelow);
            var clock = Stopwatch.StartNew();
            var verdicts = new List<EvidenceVerdict>();
            foreach (var r in rows) verdicts.Add(await grader.GradeAsync(r.Q.Question, r.Context));
            Row($"Calibrated P, correct from {from:0.0}", verdicts, $"{clock.Elapsed.TotalMilliseconds * 1000 / rows.Count:0} µs");
        }

        if (!llm) return;
        var recorded = new RecordedChatClient(Lab.LiveChat(), Path.Combine(Lab.Runs, "grader-recordings.jsonl"));
        var llmGrader = new LlmEvidenceGrader(recorded);
        var llmVerdicts = new List<EvidenceVerdict>();
        foreach (var r in rows) llmVerdicts.Add(await llmGrader.GradeAsync(r.Q.Question, r.Context));
        var seconds = File.ReadLines(Path.Combine(Lab.Runs, "grader-recordings.jsonl")).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("TotalMs").GetDouble() / 1000).Order().ToList();
        Row("llama3.2: answers / partly / does_not", llmVerdicts, $"{seconds[seconds.Count / 2]:0.0} s p50");

        // Where the LLM grader and the truth disagree, for the text.
        Console.WriteLine();
        foreach (var (r, v) in rows.Zip(llmVerdicts).Where(p => p.First.Sufficient != (p.Second.Grade is EvidenceGrade.Correct)).Take(12))
        {
            Console.WriteLine($"  {r.Q.Id,-8} {(r.Sufficient ? "sufficient  " : "insufficient")} P {r.Context.BestProbability:0.00}  llama3.2 {v.Grade,-9} {Shorten(v.Reason, 80)}");
        }
    }

    /// <summary>What each correction does to the evidence of the questions the CPU grader doesn't call correct.</summary>
    public static async Task CorrectionsAsync(Lab lab, Pipelines pipelines)
    {
        var (assembler, compressor) = (pipelines.Assembler(), pipelines.Compressor());
        var grader = new CalibratedEvidenceGrader(Pipelines.CorrectFrom, Pipelines.IncorrectBelow);
        var doubtful = new List<(GoldenQuestion Q, EvidenceVerdict Verdict, bool Sufficient)>();
        foreach (var q in lab.Questions)
        {
            var result = await pipelines.Retriever.RetrieveAsync(new RetrievalRequest(q.Question, People.Find(q.Asker)!, 4));
            var context = assembler.Assemble(result.Hits);
            if (!context.IsEmpty) context = compressor.Compress(q.Question, context);
            var verdict = await grader.GradeAsync(q.Question, context);
            if (verdict.Grade is not EvidenceGrade.Correct) doubtful.Add((q, verdict, Scoring.Holds(lab, q, context.Blocks)));
        }

        var answerable = doubtful.Where(d => d.Q.Answerable).ToList();
        Console.WriteLine($"Golden set v2, {lab.Split}: {doubtful.Count} questions graded below correct ({doubtful.Count(d => d.Verdict.Grade is EvidenceGrade.Ambiguous)} ambiguous, " +
            $"{doubtful.Count(d => d.Verdict.Grade is EvidenceGrade.Incorrect)} incorrect); {answerable.Count} answerable, {answerable.Count(d => d.Sufficient)} with sufficient evidence already");
        if (answerable.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine($"{"Correction",-36} {"Sufficient before",18} {"after",6} {"Graded correct after",21} {"ms p50",8} {"LLM calls",10}");
        var rewriter = pipelines.Rewriter();
        var strategies = new ICorrectionStrategy[]
        {
            new RewriteAndRetry(pipelines.Retriever, assembler, compressor, rewriter, pipelines.Cache),
            new WidenEvidence(pipelines.Retriever, pipelines.Assembler(8), pipelines.Compressor(800)),
            new DecomposeAndRetrieve(pipelines.Retriever, pipelines.Assembler(6), pipelines.Compressor(800), rewriter, pipelines.Cache),
        };
        foreach (var strategy in strategies)
        {
            var (after, correct, ms, calls) = (0, 0, new List<double>(), 0);
            foreach (var d in answerable)
            {
                var result = await strategy.CorrectAsync(d.Q.Question, People.Find(d.Q.Asker)!);
                after += Scoring.Holds(lab, d.Q, result.Context.Blocks) ? 1 : 0;
                correct += (await grader.GradeAsync(d.Q.Question, result.Context)).Grade is EvidenceGrade.Correct ? 1 : 0;
                (ms, calls) = ([.. ms, result.Milliseconds], calls + result.ModelCalls);
            }

            ms.Sort();
            Console.WriteLine($"{strategy.Name,-36} {$"{answerable.Count(d => d.Sufficient)}/{answerable.Count}",18} {after,6} {correct,21} {ms[ms.Count / 2],8:0} {calls,10}");
        }
    }

    /// <summary>
    /// Verification of the single pass's answers: claims the cited block supports, claims another block supports and
    /// claims nothing supports, then the same verdicts against Chapter 15's labels for the dev sample, and the
    /// re-citations against this chapter's labels at each threshold for moving a citation.
    /// </summary>
    public static void Verify(Lab lab, Pipelines pipelines, bool detail)
    {
        var outcomes = Load("single", lab.Split);
        if (outcomes.Count == 0) throw new InvalidOperationException("Run `run --config single` on this split first.");
        var verifier = pipelines.Verifier();
        var verifications = outcomes.Values.Where(o => o.Result.Answer.Claims.Count > 0).ToDictionary(o => o.Id, o => (o.Result.Answer, Check: verifier.Verify(o.Result.Answer)));
        var checks = verifications.Values.SelectMany(v => v.Check.Checks).ToList();
        Console.WriteLine($"Golden set v2, {lab.Split}: {verifications.Count} answers with claims, {checks.Count} claims checked by NLI at {verifier.Threshold:0.00}");
        Console.WriteLine($"  supported by the block they cite   {checks.Count(c => c.Supported),4}");
        Console.WriteLine($"  supported by another block         {checks.Count(c => c.Repairable),4}   re-cited (from {verifier.ReciteFrom:0.00})");
        Console.WriteLine($"  supported by no block              {checks.Count(c => !c.Supported && !c.Repairable),4}   dropped, or fed back to the model");
        Console.WriteLine($"  answers with a claim no block supports  {verifications.Values.Count(v => v.Check.Unsupported > 0)}");
        Console.WriteLine($"  answers that repair leaves with nothing {verifications.Values.Count(v => verifier.Repair(v.Answer, v.Check).Status is AnswerStatus.NotFound && v.Answer.Status is not AnswerStatus.NotFound)}");

        var labels = Path.GetFullPath(Path.Combine(Lab.Runs, "..", "..", "Chapter15-Answer-Evaluation", "Ch15.Evaluation.Cli", "labels", "claims.v1.jsonl"));
        if (lab.Split == "dev" && File.Exists(labels))
        {
            var claims = File.ReadLines(labels).Select(l => JsonSerializer.Deserialize<ClaimLabel>(l, JsonSerializerOptions.Web)!).ToList();
            Console.WriteLine();
            Console.WriteLine($"Against Chapter 15's labels ({claims.Count} claims):   {"kept",6} {"re-cited",9} {"dropped",8}");
            foreach (var supported in new[] { true, false })
            {
                var verdicts = claims.Where(p => p.Supported == supported && verifications.ContainsKey(p.Id))
                    .Select(p => verifications[p.Id].Check.Checks.FirstOrDefault(c => c.Index == p.Claim)).Where(c => c is not null).ToList();
                Console.WriteLine($"  {$"labeled {(supported ? "supported" : "unsupported")}",-22} {verdicts.Count,4} claims:       {verdicts.Count(c => c!.Supported),6} {verdicts.Count(c => c!.Repairable),9} {verdicts.Count(c => !c!.Supported && !c.Repairable),8}");
            }
        }

        Recitations(lab, verifications, detail);
    }

    /// <summary>
    /// The claims their own block doesn't support, by the best score another block gives them, against the
    /// labels for the re-citations made at 0.05: how many citations each threshold would move, and how many of those
    /// the new block really supports.
    /// </summary>
    private static void Recitations(Lab lab, Dictionary<string, (Answer Answer, Verification Check)> verifications, bool detail)
    {
        var path = Path.GetFullPath(Path.Combine(Lab.Runs, "..", "..", "Chapter16-Corrective-RAG", "Ch16.Corrective.Cli", "labels", "recitations.v1.jsonl"));
        var recitations = File.ReadLines(path).Select(l => JsonSerializer.Deserialize<RecitationLabel>(l, JsonSerializerOptions.Web)!).Where(p => p.Split == lab.Split).ToList();
        var candidates = verifications.SelectMany(v => v.Value.Check.Checks.Where(c => !c.Supported)
            .Select(c => (Id: v.Key, v.Value.Answer.Claims[c.Index].Text, c.Support, c.BetterSupport))).ToList();
        Console.WriteLine();
        Console.WriteLine($"Re-citations against the labels ({recitations.Count}, made from 0.05):");
        Console.WriteLine($"  {"re-cite from",12} {"moved",6} {"right",6} {"wrong",6}");
        foreach (var from in new[] { 0.05, 0.2, 0.5, 0.8, 0.9 })
        {
            var verdicts = candidates.Where(c => c.BetterSupport >= from).Select(c => recitations.First(p => p.Id == c.Id && p.Claim == c.Text).Supported).ToList();
            Console.WriteLine($"  {from,12:0.00} {verdicts.Count,6} {verdicts.Count(s => s),6} {verdicts.Count(s => !s),6}");
        }

        if (!detail) return;
        foreach (var c in candidates.Where(c => c.BetterSupport >= 0.05).OrderByDescending(c => c.BetterSupport))
        {
            var label = recitations.First(p => p.Id == c.Id && p.Claim == c.Text);
            Console.WriteLine($"  {c.Id,-8} own {c.Support:0.000} other {c.BetterSupport:0.000} {(label.Supported ? "right" : "WRONG"),-5} {Shorten(c.Text, 70)}");
        }
    }

    /// <summary>
    /// Every configuration scored from its recorded outcomes: Chapter 14's checks, support, abstention and cost, with
    /// paired intervals against the single pass.
    /// </summary>
    public static void Compare(Lab lab, IReadOnlyList<string> configs, CitationScorer scorer, bool diff = false)
    {
        var scores = configs.ToDictionary(c => c, c => Load(c, lab.Split).Values.Select(o => Scoring.Score(lab, lab.Questions.First(q => q.Id == o.Id), o, scorer)).ToDictionary(s => s.Id));
        var unanswerable = lab.Questions.Count(q => !q.Answerable);
        Console.WriteLine($"Golden set v2, {lab.Split}: {lab.Questions.Count - unanswerable} answerable and {unanswerable} unanswerable questions; support = NLI at 0.05 on each claim's cited block");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-18} {"Answered",8} {"Facts cited",11} {"Key values",10} {"Supported",9} {"Declined",9} {"Calls",6} {"Tokens",7} {"s p50",6} {"s p95",6} {"Escalated",9}");
        foreach (var (config, s) in scores)
        {
            var answerable = s.Values.Where(x => x.Answerable).ToList();
            var seconds = s.Values.Select(x => x.Seconds).Order().ToList();
            Console.WriteLine($"{config,-18} {answerable.Average(x => x.Declined ? 0.0 : 1.0),8:0.00} {answerable.Average(x => x.FactsCited),11:0.00} {Mean(answerable.Select(x => x.KeyValues)),10:0.00} " +
                $"{Mean(s.Values.Select(x => x.Supported)),9:0.00} {$"{s.Values.Count(x => !x.Answerable && x.Declined)}/{unanswerable}",9} {s.Values.Average(x => x.ModelCalls),6:0.00} " +
                $"{s.Values.Average(x => x.PromptTokens),7:0} {seconds[seconds.Count / 2],6:0.0} {seconds[(int)(seconds.Count * 0.95)],6:0.0} {s.Values.Count(x => x.Escalated),9}");
        }

        var baseline = scores[configs[0]];
        Console.WriteLine();
        Console.WriteLine($"Against {configs[0]}: {"facts cited",-22} {"supported",-22} {"key values",-22}");
        foreach (var config in configs.Skip(1))
        {
            var ids = baseline.Keys.Intersect(scores[config].Keys).Where(id => baseline[id].Answerable).ToList();
            var facts = Bootstrap.Paired([.. ids.Select(id => baseline[id].FactsCited)], [.. ids.Select(id => scores[config][id].FactsCited)]);
            var both = ids.Where(id => !double.IsNaN(baseline[id].Supported) && !double.IsNaN(scores[config][id].Supported)).ToList();
            var supported = Bootstrap.Paired([.. both.Select(id => baseline[id].Supported)], [.. both.Select(id => scores[config][id].Supported)]);
            var keyed = ids.Where(id => !double.IsNaN(baseline[id].KeyValues) && !double.IsNaN(scores[config][id].KeyValues)).ToList();
            var values = Bootstrap.Paired([.. keyed.Select(id => baseline[id].KeyValues)], [.. keyed.Select(id => scores[config][id].KeyValues)]);
            Console.WriteLine($"  {config,-18} {Lab.Interval(facts),-22} {Lab.Interval(supported),-22} {Lab.Interval(values),-22}");
        }

        if (!diff) return;
        for (var i = 1; i < configs.Count; i++)
        {
            var (before, after) = (scores[configs[i - 1]], scores[configs[i]]);
            Console.WriteLine();
            Console.WriteLine($"Questions {configs[i]} answers differently from {configs[i - 1]} (declined, facts cited, key values):");
            foreach (var id in before.Keys.Intersect(after.Keys))
            {
                var (a, b) = (before[id], after[id]);
                if (a.Declined == b.Declined && Same(a.FactsCited, b.FactsCited) && Same(a.KeyValues, b.KeyValues)) continue;
                Console.WriteLine($"  {id,-8} {(a.Answerable ? "" : "unanswerable"),-12} {(a.Declined ? "declined" : "answered"),8} > {(b.Declined ? "declined" : "answered"),-8}" +
                    $"  {a.FactsCited,4:0.00} > {b.FactsCited,4:0.00}  {a.KeyValues,4:0.00} > {b.KeyValues,4:0.00}");
            }
        }

        static bool Same(double x, double y) => x.Equals(y) || (double.IsNaN(x) && double.IsNaN(y));
    }

    /// <summary>One question through the corrective loop, step by step, with what each step cost.</summary>
    public static async Task TraceAsync(Lab lab, Pipelines pipelines, string id, string config)
    {
        var question = lab.Questions.First(q => q.Id == id);
        var outcome = await pipelines.AnswerAsync(config, question, lab.Split);
        Console.WriteLine($"{id}: {question.Question}  ({config}, {outcome.Route})");
        foreach (var step in outcome.Result.Steps) Console.WriteLine($"  {step.State,-10} {step.Milliseconds,8:0} ms  {Shorten(step.Detail, 96)}");
        var r = outcome.Result;
        Console.WriteLine($"  {r.Answer.Status}: {Shorten(r.Answer.Text, 140)}");
        Console.WriteLine($"  {r.ModelCalls} model calls, {r.PromptTokens} prompt tokens, {r.Retries} retries, {r.Seconds:0.0} s{(r.Exhausted is null ? "" : $", budget hit: {r.Exhausted}")}");
    }

    /// <summary>The loop as an Agent Framework workflow against the state machine: same steps, same answers?</summary>
    public static async Task WorkflowAsync(Lab lab, Pipelines pipelines, IReadOnlyList<GoldenQuestion> questions)
    {
        var (same, clock) = (0, Stopwatch.StartNew());
        var workflow = CragWorkflow.Build(pipelines.Crag(lab.Split, "workflow"));
        File.WriteAllText(Path.Combine(Lab.Runs, "crag-workflow.mmd"), Microsoft.Agents.AI.Workflows.WorkflowVisualizer.ToMermaidString(workflow));
        foreach (var q in questions)
        {
            var asker = People.Find(q.Asker)!;
            var machine = await pipelines.Crag(lab.Split, "workflow").RunAsync(q.Question, asker);
            var run = new CragRun(q.Question, asker, new BudgetMeter(new LoopBudget()));
            var answer = await CragWorkflow.RunAsync(workflow, run);
            var match = answer.Text == machine.Answer.Text && run.Steps.Select(s => s.State).SequenceEqual(machine.Steps.Select(s => s.State));
            same += match ? 1 : 0;
            Console.WriteLine($"{q.Id,-8} {string.Join(" > ", run.Steps.Select(s => s.State)),-60} {(match ? "same" : "DIFFERENT")}");
        }

        Console.WriteLine($"{same} of {questions.Count} questions: the same steps and the same answer ({clock.Elapsed.TotalSeconds:0.0} s)");
    }

    /// <summary>
    /// The verify configuration with the model unreachable (stop Ollama first). Calls bypass the recordings; the circuit
    /// breaker opens after three failures, and every answer degrades to the documents that look relevant.
    /// </summary>
    public static async Task OutageAsync(Pipelines pipelines, IReadOnlyList<GoldenQuestion> questions)
    {
        var live = Lab.LiveChat();
        var breaker = live.GetService<ModelCircuitBreaker>()!;
        var loop = new CorrectiveRag(pipelines.Retriever, pipelines.Assembler(), pipelines.Compressor(), pipelines.Generator(live), LoopBudget.SinglePass, verifier: pipelines.Verifier());
        foreach (var q in questions)
        {
            var clock = Stopwatch.StartNew();
            var answer = (await loop.RunAsync(q.Question, People.Find(q.Asker)!)).Answer;
            Console.WriteLine($"{q.Id,-8} {clock.Elapsed.TotalMilliseconds,6:0} ms  circuit {(breaker.IsOpen ? "open  " : "closed")}  {Shorten(answer.Problems.FirstOrDefault() ?? "", 84)}");
            Console.WriteLine($"         {Shorten(answer.Text, 110)}");
        }
    }

    private static double Mean(IEnumerable<double> values) => values.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Average();

    public static string Shorten(string text, int length)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= length ? flat : flat[..(length - 1)] + "…";
    }

    /// <summary>A claim label as Chapter 15 wrote it.</summary>
    private sealed record ClaimLabel(string Id, int Claim, string Text, string Source, bool Supported, string? Note = null);

    /// <summary>Whether the block a claim was re-cited to supports it, as labeled (labels/recitations.v1.jsonl).</summary>
    private sealed record RecitationLabel(string Id, string Split, string Claim, string From, string To, bool Supported, string Note);
}
