using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;

namespace Ch14.Generation.Cli;

public static class Commands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>
    /// What each assembly step does to the evidence for every question: its size in tokens, whether it still holds
    /// every labeled fact, and how often nothing passes the threshold. No model is called.
    /// </summary>
    public static async Task ContextAsync(Lab lab)
    {
        using var crossEncoder = Lab.CrossEncoder();
        var retriever = await lab.RetrieverAsync(crossEncoder);
        var compressor = new ExtractiveCompressor(crossEncoder, Lab.O200k, 500);
        var steps = new (string Name, ContextOptions Options, bool Compress)[]
        {
            ("four parents as retrieved", new() { AbstainBelow = 0, MergeAdjacent = false, TableRowsAsKeyValues = false }, false),
            ("+ merged, rows with column names", new() { AbstainBelow = 0 }, false),
            ("+ abstain below 0.005", new(), false),
            ("budget of 500 tokens", new() { MaxTokens = 500 }, false),
            ("best sentences, 500 tokens", new(), true),
            ("drop parents below 0.1", new() { MinProbability = 0.1 }, false),
        };

        var rows = steps.Select(_ => new List<(GoldenQuestion Q, AssembledContext C)>()).ToList();
        var (retrieval, compression) = (new List<double>(), new List<double>());
        var retrieved = new List<(GoldenQuestion Q, IReadOnlyList<Hit> Hits)>();
        foreach (var question in lab.Questions)
        {
            var clock = Stopwatch.StartNew();
            var hits = (await retriever.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, 4))).Hits;
            retrieval.Add(clock.Elapsed.TotalMilliseconds);
            retrieved.Add((question, hits));
            for (var i = 0; i < steps.Length; i++)
            {
                var context = new ContextAssembler(Lab.Calibration, Lab.O200k, steps[i].Options).Assemble(hits);
                if (steps[i].Compress && !context.IsEmpty)
                {
                    clock.Restart();
                    context = compressor.Compress(question.Question, context);
                    compression.Add(clock.Elapsed.TotalMilliseconds);
                }

                rows[i].Add((question, context));
            }
        }

        var unanswerable = lab.Questions.Count(q => !q.Answerable);
        Console.WriteLine($"Golden set v2, {lab.Split}: {lab.Answerable.Count} answerable and {unanswerable} unanswerable questions; hybrid retrieval re-ranked by MiniLM-L6, top 4 of 8");
        Console.WriteLine();
        Console.WriteLine($"{"Evidence",-38} {"Tokens p50",10} {"p95",6} {"Holds every fact",17} {"Empty: answerable",18} {"unanswerable",13}");
        for (var i = 0; i < steps.Length; i++)
        {
            var answerable = rows[i].Where(r => r.Q.Answerable).ToList();
            var tokens = rows[i].Where(r => !r.C.IsEmpty).Select(r => (double)r.C.Tokens).Order().ToList();
            var holds = answerable.Average(r => Holds(lab, r.Q, r.C.Blocks));
            Console.WriteLine($"{steps[i].Name,-38} {Percentile(tokens, 0.5),10:0} {Percentile(tokens, 0.95),6:0} {holds,17:0.00} {answerable.Count(r => r.C.IsEmpty),18} {rows[i].Count(r => !r.Q.Answerable && r.C.IsEmpty),13}");
        }

        var notes = rows[2].SelectMany(r => r.C.Notes).ToList();
        Console.WriteLine();
        Console.WriteLine($"Merged parents: {notes.Count(n => n.StartsWith("merged"))}; repeats dropped: {notes.Count(n => n.StartsWith("dropped a repeat"))}; " +
            $"blocks dropped for the budget: {notes.Count(n => n.Contains("for the budget"))}");
        Console.WriteLine($"Retrieval and re-ranking: {Percentile([.. retrieval.Order()], 0.5):0} ms p50; compression: {Percentile([.. compression.Order()], 0.5):0} ms p50");

        // The same threshold used two ways: to abstain when even the best parent is below it, and to drop weak parents.
        Console.WriteLine();
        Console.WriteLine($"{"Threshold",9} {"Abstain: answerable",20} {"unanswerable",13} {"Drop weaker parents: holds every fact",38} {"tokens p50",11}");
        foreach (var threshold in new[] { 0.0, 0.005, 0.01, 0.02, 0.05, 0.1 })
        {
            var best = retrieved.Select(r => (r.Q, Best: r.Hits.Count == 0 ? 0 : r.Hits.Max(h => Lab.Calibration.Probability(h.Score)))).ToList();
            var dropped = retrieved.Select(r => (r.Q, C: new ContextAssembler(Lab.Calibration, Lab.O200k, new() { AbstainBelow = 0, MinProbability = threshold }).Assemble(r.Hits))).ToList();
            Console.WriteLine($"{threshold,9:0.000} {best.Count(b => b.Q.Answerable && b.Best < threshold),20} {best.Count(b => !b.Q.Answerable && b.Best < threshold),13} " +
                $"{dropped.Where(d => d.Q.Answerable).Average(d => Holds(lab, d.Q, d.C.Blocks)),38:0.00} {Percentile([.. dropped.Where(d => !d.C.IsEmpty).Select(d => (double)d.C.Tokens).Order()], 0.5),11:0}");
        }

        Console.WriteLine("Best parent of each unanswerable question: " + string.Join(", ", retrieved.Where(r => !r.Q.Answerable)
            .Select(r => $"{r.Q.Id} {(r.Hits.Count == 0 ? 0 : r.Hits.Max(h => Lab.Calibration.Probability(h.Score))):0.000}")));

        Console.WriteLine("Answerable questions abstained on at 0.005: " + string.Join(", ", rows[2].Where(r => r.Q.Answerable && r.C.IsEmpty).Select(r => r.Q.Id)));
        var example = rows[2].First(r => r.Q.Id == "G001");
        Console.WriteLine();
        Console.WriteLine($"{example.Q.Id} \"{example.Q.Question}\", {example.C.Tokens} tokens:");
        foreach (var block in example.C.Blocks)
        {
            Console.WriteLine($"  {block.Id} {block.DocumentId} p={block.Probability:0.00} {block.Section}");
            foreach (var line in block.Text.Split('\n').Take(3)) Console.WriteLine($"     {Shorten(line, 100)}");
        }
    }

    /// <summary>Answers questions with one setup, records each answer and prints a summary. Reruns resume.</summary>
    public static async Task AnswerAsync(Lab lab, string setup, IReadOnlyList<GoldenQuestion> questions)
    {
        using var crossEncoder = Lab.CrossEncoder();
        var retriever = await lab.RetrieverAsync(crossEncoder);
        var chat = Lab.Chat();
        var ask = new Setups(retriever, crossEncoder, chat).Get(setup);
        var path = AnswersPath(setup, lab.Split);
        var done = Load(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var question in questions.Where(q => !done.ContainsKey(q.Id)))
        {
            var answer = await ask(question.Question, People.Find(question.Asker)!, CancellationToken.None);
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(new Recorded(question.Id, answer), Json) + "\n");
            done[question.Id] = answer;
            Console.WriteLine($"{question.Id,-8} {answer.Status,-9} verified {answer.Claims.Count(c => c.Valid)}/{answer.Claims.Count} " +
                $"prompt {answer.Timings.PromptTokens,5} first {answer.Timings.FirstTokenMs / 1000,5:0.0} s total {answer.Timings.TotalMs / 1000,5:0.0} s {Shorten(answer.Text, 60)}");
        }

        Console.WriteLine($"{setup}: {questions.Count} questions; {chat.Recorded} model calls recorded, {chat.Replayed} replayed");
    }

    /// <summary>Scores recorded answers of several setups and compares each with the first.</summary>
    public static void Compare(Lab lab, IReadOnlyList<string> setups, IReadOnlyList<GoldenQuestion> questions)
    {
        var ids = questions.Select(q => q.Id).ToHashSet();
        var scored = setups.Select(s => (Setup: s, Scores: Load(AnswersPath(s, lab.Split))
            .Where(a => ids.Contains(a.Key))
            .Select(a => Scoring.Score(questions.First(q => q.Id == a.Key), a.Value, lab.Spans(questions.First(q => q.Id == a.Key)), lab.Documents))
            .OrderBy(s => s.Id, StringComparer.Ordinal).ToList())).ToList();

        var answerable = questions.Count(q => q.Answerable);
        Console.WriteLine($"Golden set v2 ({lab.GoldenSetSha256[..12]}), {lab.Split}: {answerable} answerable and {questions.Count - answerable} unanswerable questions; llama3.2 on the CPU");
        Console.WriteLine();
        Console.WriteLine($"{"Setup",-14} {"Answered",8} {"Facts cited",11} {"On target",9} {"Verified",8} {"Key values",10} {"Abstained",9} {"Prompt",7} {"First token",11} {"Total",6}");
        foreach (var (setup, scores) in scored)
        {
            var a = scores.Where(s => s.Answerable).ToList();
            var u = scores.Where(s => !s.Answerable).ToList();
            Console.WriteLine($"{setup,-14} {a.Average(s => s.Answered),8:0.00} {a.Average(s => s.FactsCited),11:0.00} {Mean(a, s => s.CitationsOnTarget),9:0.00} " +
                $"{Mean(scores, s => s.ClaimsVerified),8:0.00} {Mean(a, s => s.KeyValues),10:0.00} {u.Count(s => s.Abstained),5}/{u.Count,-3} " +
                $"{Median(scores.Where(s => s.PromptTokens > 0), s => s.PromptTokens),7:0} {Median(scores.Where(s => s.PromptTokens > 0), s => s.FirstTokenMs) / 1000,9:0.0} s {Median(scores.Where(s => s.PromptTokens > 0), s => s.TotalMs) / 1000,4:0.0} s");
        }

        Console.WriteLine();
        foreach (var (setup, scores) in scored)
        {
            var declined = scores.Where(s => s.Answerable && s.Abstained).ToList();
            Console.WriteLine($"{setup,-14} declined {declined.Count} answerable questions, {declined.Count(s => s.EvidenceSufficient == 1)} with every fact in the evidence");
        }

        Console.WriteLine();
        var baseline = scored[0];
        foreach (var (setup, scores) in scored.Skip(1))
        {
            var pairs = baseline.Scores.Where(s => s.Answerable).Join(scores.Where(s => s.Answerable), b => b.Id, c => c.Id, (b, c) => (B: b, C: c)).ToList();
            var facts = Bootstrap.Paired([.. pairs.Select(p => p.B.FactsCited)], [.. pairs.Select(p => p.C.FactsCited)]);
            var keyed = pairs.Where(p => !double.IsNaN(p.B.KeyValues) && !double.IsNaN(p.C.KeyValues)).ToList();
            var values = Bootstrap.Paired([.. keyed.Select(p => p.B.KeyValues)], [.. keyed.Select(p => p.C.KeyValues)]);
            Console.WriteLine($"{setup,-14} against {baseline.Setup}: facts cited {Lab.Interval(facts)}, key values {Lab.Interval(values)} ({pairs.Count} questions)");
            Console.WriteLine($"{"",-14} answered, declined by {baseline.Setup}: {string.Join(", ", pairs.Where(p => p.B.Abstained && !p.C.Abstained).Select(p => p.C.Id))}");
            Console.WriteLine($"{"",-14} declined, answered by {baseline.Setup}: {string.Join(", ", pairs.Where(p => !p.B.Abstained && p.C.Abstained).Select(p => p.C.Id))}");
        }

        Console.WriteLine();
        foreach (var (setup, scores) in scored.Skip(1))
        {
            var cited = baseline.Scores.Join(scores, b => b.Id, c => c.Id, (b, c) => (B: b, C: c))
                .Where(p => p.B.Answerable && !double.IsNaN(p.B.CitationsOnTarget) && !double.IsNaN(p.C.CitationsOnTarget)).ToList();
            var onTarget = Bootstrap.Paired([.. cited.Select(p => p.B.CitationsOnTarget)], [.. cited.Select(p => p.C.CitationsOnTarget)]);
            Console.WriteLine($"{setup,-14} against {baseline.Setup}: citations on target {Lab.Interval(onTarget)} ({cited.Count} questions that cite in both)");
        }
    }

    /// <summary>
    /// What validation found in one setup's recorded claims: how many cite a real source, how many quotes are in it,
    /// and why the others aren't: copied from another source, rebuilt as table syntax, nearly copied or changed.
    /// </summary>
    public static void Quotes(Lab lab, string setup)
    {
        var answers = Load(AnswersPath(setup, lab.Split)).Values.ToList();
        var claims = answers.SelectMany(a => a.Claims.Select(c => (Answer: a, Claim: c))).ToList();
        var cited = claims.Where(x => x.Claim.Cited).ToList();
        var real = cited.Where(x => x.Claim.SourceExists).ToList();
        var missing = real.Where(x => !x.Claim.QuoteFound).ToList();
        string Why((Answer Answer, Claim Claim) x)
        {
            if (x.Answer.Evidence.Any(b => b.Id != x.Claim.Source && CitationValidator.Locate(x.Claim.Quote, b) is not null)) return "word for word, but in another source";
            if (x.Claim.Quote.Contains('|') || x.Claim.Quote.Contains("---", StringComparison.Ordinal)) return "rebuilt with Markdown table syntax";
            var text = x.Answer.Evidence.First(b => b.Id == x.Claim.Source).Text;
            var words = x.Claim.Quote.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "no quote at all";
            var run = Enumerable.Range(0, words.Length).Max(i => Enumerable.Range(1, words.Length - i)
                .Where(n => text.Contains(string.Join(' ', words, i, n), StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(0).Max());
            return run >= 0.75 * words.Length ? "nearly copied: three quarters in a row" : "changed or invented";
        }

        Console.WriteLine($"{setup}, {lab.Split}: {answers.Count} answers, {claims.Count} claims; {cited.Count} cite a source, {real.Count} a source the model was given; " +
            $"{real.Count - missing.Count} quotes found in it");
        foreach (var group in missing.GroupBy(Why).OrderByDescending(g => g.Count()))
        {
            var example = group.First();
            Console.WriteLine($"  {group.Count(),3}  {group.Key}: {example.Claim.Source} \"{Shorten(example.Claim.Quote, 70)}\"");
        }
    }

    /// <summary>Prompt tokens against time to first token over every recorded answer: the CPU's prefill rate.</summary>
    public static void Latency(Lab lab, IReadOnlyList<string> setups)
    {
        var answers = setups.SelectMany(s => Load(AnswersPath(s, lab.Split)).Values).Where(a => a.Timings.PromptTokens > 0).ToList();
        var o200k = answers.Where(a => a.PromptVersion != "chapter3").Select(a => (double)GroundingPrompt.Build("", People.Tomas, a.Evidence, new DateOnly(2027, 2, 1)).Sum(m => Lab.O200k.CountTokens(m.Text))).ToList();
        var llama = answers.Where(a => a.PromptVersion != "chapter3").Select(a => (double)a.Timings.PromptTokens).ToList();
        var x = answers.Select(a => (double)a.Timings.PromptTokens).ToArray();
        var y = answers.Select(a => (a.Timings.FirstTokenMs - a.Timings.RetrievalMs) / 1000).ToArray();
        var (slope, intercept) = Fit(x, y);
        var decode = answers.Where(a => a.Timings.OutputTokens > 10).Select(a => a.Timings.OutputTokens / ((a.Timings.TotalMs - a.Timings.FirstTokenMs) / 1000)).Order().ToList();
        var system = Lab.O200k.CountTokens(GroundingPrompt.Build("", People.Tomas, [], new DateOnly(2027, 2, 1))[0].Text);
        Console.WriteLine($"{answers.Count} recorded answers on {lab.Split}; the grounding instructions are {system} o200k tokens");
        Console.WriteLine($"Prompt tokens, llama3.2 against o200k: x{llama.Sum() / o200k.Sum():0.00}");
        Console.WriteLine($"Time to first token = {intercept:0.0} s + {slope * 1000:0.0} s per 1,000 prompt tokens ({1 / slope:0} tokens a second)");
        Console.WriteLine($"Decode: {Percentile(decode, 0.5):0.0} tokens a second (p50)");
        foreach (var band in new[] { 500, 1_000, 1_500, 2_000, 3_000 })
        {
            var near = answers.Where(a => Math.Abs(a.Timings.PromptTokens - band) < 250).Select(a => (a.Timings.FirstTokenMs - a.Timings.RetrievalMs) / 1000).Order().ToList();
            if (near.Count > 0) Console.WriteLine($"  prompts of {band - 250:N0} to {band + 250:N0} tokens: first token after {Percentile(near, 0.5):0.0} s (p50 of {near.Count})");
        }
    }

    /// <summary>One recorded answer in full: the evidence, the text, each citation and what validation said.</summary>
    public static void Show(Lab lab, string setup, string id)
    {
        var answer = Load(AnswersPath(setup, lab.Split))[id];
        var question = lab.Questions.First(q => q.Id == id);
        Console.WriteLine($"{id} \"{question.Question}\" ({setup})");
        foreach (var block in answer.Evidence) Console.WriteLine($"  {block.Id} {block.DocumentId} p={block.Probability:0.00} {block.CountTokens(Lab.O200k)} tokens  {block.Section}");
        Console.WriteLine($"Status: {answer.Status}; confidence {answer.Confidence:0.00}; prompt {answer.PromptVersion}");
        Console.WriteLine(answer.Text);
        foreach (var c in answer.Claims) Console.WriteLine($"  {(c.Valid ? "verified" : !c.Cited ? "no source" : c.SourceExists ? "QUOTE NOT FOUND" : "NO SUCH SOURCE")} [{c.Source}] \"{c.Quote}\"{(c.Start >= 0 ? $" at {c.DocumentId} {c.Start}-{c.End}" : "")}");
        foreach (var p in answer.Problems) Console.WriteLine($"  problem: {p}");
        Console.WriteLine($"Prompt {answer.Timings.PromptTokens} tokens, first token {answer.Timings.FirstTokenMs / 1000:0.0} s, total {answer.Timings.TotalMs / 1000:0.0} s, {answer.Timings.OutputTokens} tokens out");
    }

    /// <summary>
    /// Golden set v2's two-part questions asked as a question and a follow-up (Chapter 13): the follow-up answered on
    /// its own and with the conversation, scored against the whole question; then "make that shorter" once.
    /// </summary>
    public static async Task TurnsAsync(Lab lab)
    {
        using var crossEncoder = Lab.CrossEncoder();
        var retriever = await lab.RetrieverAsync(crossEncoder);
        var chat = Lab.Chat();
        var generator = new Setups(retriever, crossEncoder, chat).Grounded();
        var conversations = Conversations.From(lab.Answerable);
        var (alone, joined) = (new List<AnswerScore>(), new List<AnswerScore>());
        (Turn First, Turn Second)? example = null;
        foreach (var c in conversations)
        {
            var asker = People.Find(c.Original.Asker)!;
            var spans = lab.Spans(c.Original);
            var first = new Turn(c.First, await generator.AnswerAsync(c.First, asker));
            var single = await generator.AnswerAsync(c.FollowUp, asker);
            var withHistory = await generator.AnswerAsync(c.FollowUp, asker, [first]);
            alone.Add(Scoring.Score(c.Original, single, spans, lab.Documents));
            joined.Add(Scoring.Score(c.Original, withHistory, spans, lab.Documents));
            Console.WriteLine($"{c.Original.Id,-8} {Shorten(c.FollowUp, 44),-44} alone: {single.Status,-9} with the conversation: {withHistory.Status}");
            if (c.First.Contains("E-217", StringComparison.Ordinal)) example = (first, new Turn(c.FollowUp, withHistory));
        }

        Console.WriteLine();
        Console.WriteLine($"Golden set v2, {lab.Split}: {conversations.Count} follow-ups, scored against the whole two-part question");
        Console.WriteLine($"{"Follow-up answered",-30} {"Answered",8} {"Facts cited",11} {"Key values",10} {"Prompt",7}");
        foreach (var (name, scores) in new[] { ("on its own", alone), ("with the conversation", joined) })
        {
            Console.WriteLine($"{name,-30} {scores.Average(s => s.Answered),8:0.00} {scores.Average(s => s.FactsCited),11:0.00} {Mean(scores, s => s.KeyValues),10:0.00} {Median(scores.Where(s => s.PromptTokens > 0), s => s.PromptTokens),7:0}");
        }

        Console.WriteLine($"{chat.Recorded} model calls recorded, {chat.Replayed} replayed");
        if (example is not { } e) return;
        Console.WriteLine();
        Console.WriteLine($"Kwame: {e.First.Question}");
        Console.WriteLine($"Assistant: {e.First.Answer.Text}");
        Console.WriteLine($"Kwame: {e.Second.Question}");
        Console.WriteLine($"Assistant: {e.Second.Answer.Text}");
        Answer? shorter = null;
        await foreach (var ev in generator.StreamAsync("Make that shorter.", People.Kwame, [e.First, e.Second], e.Second.Answer.Evidence))
        {
            if (ev is AnswerReady ready) shorter = ready.Answer;
        }

        Console.WriteLine("Kwame: Make that shorter.");
        Console.WriteLine($"Assistant: {shorter!.Text}");
        Console.WriteLine($"Reworked from the same {shorter.Evidence.Count} sources without retrieving: {e.Second.Answer.Text.Length} characters to {shorter.Text.Length}; " +
            $"claims verified {shorter.Claims.Count(c => c.Valid)} of {shorter.Claims.Count}");
    }

    /// <summary>One answer streamed live from the model, with the time of each event the user would see.</summary>
    public static async Task StreamAsync(Lab lab, string id)
    {
        using var crossEncoder = Lab.CrossEncoder();
        var retriever = await lab.RetrieverAsync(crossEncoder);
        var question = lab.Questions.First(q => q.Id == id);
        // The configuration the API ships: compressed to the best sentences, 500 tokens.
        var generator = new Setups(retriever, crossEncoder, Lab.LiveChat()).Grounded(compressor: new ExtractiveCompressor(crossEncoder, Lab.O200k, 500));
        var clock = Stopwatch.StartNew();
        var text = new StringBuilder();
        var (deltas, firstText) = (0, 0.0);
        Console.WriteLine($"{question.Asker}: {question.Question}");
        await foreach (var e in generator.StreamAsync(question.Question, People.Find(question.Asker)!))
        {
            var at = clock.Elapsed.TotalSeconds;
            switch (e)
            {
                case EvidenceReady ready:
                    Console.WriteLine($"[{at,6:0.00} s] sources: " + string.Join(", ", ready.Evidence.Select(b => $"{b.Id} {b.DocumentId} ({b.Probability:0.00})")));
                    break;
                case AnswerDelta delta:
                    if (deltas++ == 0) firstText = at;
                    text.Append(delta.Text);
                    break;
                case AnswerReady done:
                    Console.WriteLine($"[{firstText,6:0.00} s] answer text begins; {deltas} pieces streamed");
                    Console.WriteLine($"[{at,6:0.00} s] answer ready: {done.Answer.Status}, {done.Answer.Claims.Count(c => c.Valid)} of {done.Answer.Claims.Count} claims verified, " +
                        $"confidence {done.Answer.Confidence:0.00}; first token at {done.Answer.Timings.FirstTokenMs / 1000:0.00} s; " +
                        $"{done.Answer.Timings.PromptTokens} prompt and {done.Answer.Timings.OutputTokens} output tokens");
                    Console.WriteLine($"Streamed:  {text}");
                    Console.WriteLine($"Validated: {done.Answer.Text}");
                    foreach (var c in done.Answer.Claims) Console.WriteLine($"  [{c.Source}] \"{c.Quote}\" {(c.Valid ? $"found at {c.DocumentId} {c.Start}-{c.End}" : "not found")}");
                    break;
            }
        }
    }

    public static IReadOnlyList<GoldenQuestion> Sample(IReadOnlyList<GoldenQuestion> questions, int answerable) =>
        [.. questions.Where(q => !q.Answerable),
            .. questions.Where(q => q.Answerable).OrderBy(q => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(q.Id))), StringComparer.Ordinal).Take(answerable)];

    private static double Holds(Lab lab, GoldenQuestion question, IReadOnlyList<EvidenceBlock> blocks) =>
        lab.Spans(question).GroupBy(s => s.Fact).All(f => f.Any(s => blocks.Any(b => Scoring.Covers(b, s)))) ? 1 : 0;

    public static string AnswersPath(string setup, string split) => Path.Combine(Lab.Runs, "answers", $"{setup}-{split}.jsonl");

    public static Dictionary<string, Answer> Load(string path) => !File.Exists(path) ? [] :
        File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<Recorded>(l, Json)!).ToDictionary(r => r.Id, r => r.Answer);

    private static double Mean(IEnumerable<AnswerScore> scores, Func<AnswerScore, double> value)
    {
        var values = scores.Select(value).Where(v => !double.IsNaN(v)).ToList();
        return values.Count == 0 ? double.NaN : values.Average();
    }

    private static double Median(IEnumerable<AnswerScore> scores, Func<AnswerScore, double> value) => Percentile([.. scores.Select(value).Order()], 0.5);

    public static double Percentile(IReadOnlyList<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];

    private static (double Slope, double Intercept) Fit(double[] x, double[] y)
    {
        var (mx, my) = (x.Average(), y.Average());
        var slope = x.Zip(y).Sum(p => (p.First - mx) * (p.Second - my)) / x.Sum(v => (v - mx) * (v - mx));
        return (slope, my - slope * mx);
    }

    public static string Shorten(string text, int length)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= length ? line : line[..(length - 1)] + "…";
    }

    private sealed record Recorded(string Id, Answer Answer);
}
