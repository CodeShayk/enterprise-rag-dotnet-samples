using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Performance;
using Microsoft.Extensions.AI;
using static System.FormattableString;

namespace Ch21.Performance.Cli;

/// <summary>One answer of one configuration: its times, tokens, CPU-seconds and quality.</summary>
public sealed record RunRow(
    string Id, string Config, string Status, bool Model, bool Escalated, double EvidenceMs, double FirstTokenMs, double TotalMs,
    long PromptTokens, long OutputTokens, double AppCpu, double ModelCpu, bool CpuMeasured,
    bool Answerable, bool Answered, double FactsCited, double KeyValues, double Verified, string Type = "", double BestProbability = 0);

/// <summary>A live answer against its recording: times in seconds, and CPU-seconds in this process and the model server.</summary>
public sealed record SpotRow(string Id, double LiveFirst, double LiveTotal, double RecFirst, double RecTotal, double Evidence, double App, double ModelCpu);

/// <summary>The model experiments: prompt layouts against the prefix cache, live spot checks, the cascade and the load test.</summary>
public static class Models
{
    // Quality scores are NaN for unanswerable questions; JSON numbers can't say NaN without the named literal.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>
    /// Prompts this server hasn't seen, straight to Ollama with one output token: the shipped layout (instructions first,
    /// the same for every request), the same prompts behind a per-request first line (as a request ID or timestamp
    /// would put there), and the shipped prompts once more. Ollama 0.34's llama.cpp server keeps earlier prompts in
    /// memory and starts from the longest prefix it has.
    /// </summary>
    public static async Task PrefixAsync(Lab lab, string split = "test", int count = 12)
    {
        var generator = await lab.GeneratorAsync(Lab.AnswerChat(Lab.Large));
        var prompts = new List<List<ChatMessage>>();
        foreach (var q in lab.Split(split))
        {
            var answer = await Lab.AnswerAsync(generator, q);
            if (answer.Evidence.Count == 0) continue;
            prompts.Add(GroundingPrompt.Build(q.Question, People.Find(q.Asker)!, answer.Evidence, new DateOnly(2027, 2, 1)));
            if (prompts.Count == count) break;
        }

        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = TimeSpan.FromMinutes(5) };
        Console.WriteLine($"{prompts.Count} {split} prompts, llama3.2 3B on the CPU, one output token each");
        Console.WriteLine($"{"Run",-50} {"prompt tokens",14} {"prefill s",10}");
        foreach (var (name, layout) in new (string, Func<List<ChatMessage>, int, List<ChatMessage>>)[]
        {
            ("instructions first, prompts not seen before", (m, _) => m),
            ("a per-request line before the instructions", (m, i) => [new ChatMessage(ChatRole.System, $"Request {i:D4}-{Guid.NewGuid():N}\n{m[0].Text}"), m[1]]),
            ("instructions first, the same prompts again", (m, _) => m),
        })
        {
            var results = new List<(long Tokens, double Seconds)>();
            for (var i = 0; i < prompts.Count; i++) results.Add(await ChatAsync(http, Lab.Large, layout(prompts[i], i)));
            Console.WriteLine(Invariant($"{name,-50} {results.Average(r => r.Tokens),14:F0} {results.Average(r => r.Seconds),10:F2}"));
        }
    }

    private static async Task<(long Tokens, double Seconds)> ChatAsync(HttpClient http, string model, List<ChatMessage> messages)
    {
        var request = new
        {
            model,
            stream = false,
            messages = messages.Select(m => new { role = m.Role.Value, content = m.Text }),
            options = new { num_predict = 1, temperature = 0, num_ctx = 8192 },
        };
        using var response = await http.PostAsJsonAsync("/api/chat", request, Json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (body.GetProperty("prompt_eval_count").GetInt64(), body.GetProperty("prompt_eval_duration").GetInt64() / 1e9);
    }

    /// <summary>
    /// Questions answered live (nothing replayed) by one model, with each answer's CPU-seconds in this process and in
    /// the model server: the check that recorded times still hold, and the CPU the cost model needs.
    /// </summary>
    public static async Task SpotCheckAsync(Lab lab, string model, int count)
    {
        var live = await lab.GeneratorAsync(Lab.LiveChat(model));
        var recorded = await lab.GeneratorAsync(Lab.AnswerChat(model));
        var meter = new CpuMeter();
        await Lab.AnswerAsync(live, lab.Split("dev").First());
        var rows = new List<SpotRow>();
        foreach (var q in lab.Split("dev"))
        {
            var replay = await Lab.AnswerAsync(recorded, q);
            if (replay.Timings.PromptTokens == 0) continue;
            var (answer, app, cpu) = await meter.MeasureAsync(() => Lab.AnswerAsync(live, q));
            rows.Add(new SpotRow(q.Id, answer.Timings.FirstTokenMs / 1000, answer.Timings.TotalMs / 1000, replay.Timings.FirstTokenMs / 1000, replay.Timings.TotalMs / 1000,
                answer.Timings.RetrievalMs / 1000, app, cpu));
            if (rows.Count == count) break;
        }

        var modelSeconds = rows.Sum(r => r.LiveTotal - r.Evidence);
        Console.WriteLine($"{model}: {rows.Count} dev answers live against their recordings");
        Console.WriteLine(Invariant($"first token p50 live {Stats.Percentile(rows.Select(r => r.LiveFirst), 0.5):F1} s, recorded {Stats.Percentile(rows.Select(r => r.RecFirst), 0.5):F1} s; answer p50 live {Stats.Percentile(rows.Select(r => r.LiveTotal), 0.5):F1} s, recorded {Stats.Percentile(rows.Select(r => r.RecTotal), 0.5):F1} s"));
        Console.WriteLine(Invariant($"CPU-seconds per answer p50: application {Stats.Percentile(rows.Select(r => r.App), 0.5):F2}, model server {Stats.Percentile(rows.Select(r => r.ModelCpu), 0.5):F1} ({rows.Sum(r => r.ModelCpu) / modelSeconds:F1} cores busy while the model ran)"));
        await File.WriteAllLinesAsync(Path.Combine(Lab.Runs, $"spot-{model.Replace(':', '-')}.jsonl"), rows.Select(r => JsonSerializer.Serialize(r, Json)));
    }

    /// <summary>
    /// The small model alone, the large model alone and the cascade between them, over a split: every answer scored and
    /// timed. The application's CPU-seconds are measured; a model's are measured on a live call and otherwise taken from
    /// its recorded time at the cores the spot check measured for that model.
    /// </summary>
    public static async Task CascadeAsync(Lab lab, string split)
    {
        var smallChat = Lab.AnswerChat(Lab.Small);
        var small = await lab.GeneratorAsync(smallChat);
        var large = await lab.GeneratorAsync(Lab.AnswerChat(Lab.Large));
        var meter = new CpuMeter();
        var (smallCores, largeCores) = (CoresWhileModelRuns(Lab.Small), CoresWhileModelRuns(Lab.Large));
        double ModelCpu(AnswerTimings t, double cores) => t.PromptTokens == 0 ? 0 : (t.TotalMs - t.RetrievalMs) / 1000 * cores;
        var rows = new List<RunRow>();
        foreach (var q in lab.Split(split))
        {
            var spans = lab.Spans(q);
            var live = smallChat.Recorded;
            var (first, app, cpu) = await meter.MeasureAsync(() => Lab.AnswerAsync(small, q));
            var measured = smallChat.Recorded > live;
            var smallCpu = measured ? cpu : ModelCpu(first.Timings, smallCores);
            var big = await Lab.AnswerAsync(large, q);
            rows.Add(Row(q, "3B", big, false, big.Timings, app, ModelCpu(big.Timings, largeCores), false, lab, spans));
            rows.Add(Row(q, "1B", first, false, first.Timings, app, smallCpu, measured, lab, spans));

            var escalate = first.Evidence.Count > 0 && !ModelCascade.Accept(first);
            var final = escalate ? await Lab.AnswerAsync(large, q, first.Evidence) : first;
            var timings = escalate
                ? first.Timings with { TotalMs = first.Timings.TotalMs + final.Timings.TotalMs, PromptTokens = first.Timings.PromptTokens + final.Timings.PromptTokens, OutputTokens = first.Timings.OutputTokens + final.Timings.OutputTokens }
                : first.Timings;
            rows.Add(Row(q, "cascade", final, escalate, timings, app, smallCpu + (escalate ? ModelCpu(final.Timings, largeCores) : 0), measured, lab, spans));
        }

        Directory.CreateDirectory(Lab.Runs);
        await File.WriteAllLinesAsync(Path.Combine(Lab.Runs, $"cascade-{split}.jsonl"), rows.Select(r => JsonSerializer.Serialize(r, Json)));
        Report(rows, split);
    }

    private static RunRow Row(GoldenQuestion q, string config, Answer answer, bool escalated, AnswerTimings t, double app, double cpu, bool measured, Lab lab, IReadOnlyList<LocatedSpan> spans)
    {
        var quality = Scoring.Score(q, answer, spans, lab.Documents);
        return new RunRow(q.Id, config, answer.Status.ToString(), t.PromptTokens > 0, escalated, t.RetrievalMs, t.FirstTokenMs, t.TotalMs,
            t.PromptTokens, t.OutputTokens, app, cpu, measured, quality.Answerable, quality.Answered, quality.FactsCited, quality.KeyValues, quality.Verified,
            q.Type, answer.Evidence.Count == 0 ? 0 : answer.Evidence.Max(b => b.Probability));
    }

    /// <summary>Cores the model server kept busy per second of a live answer, from the spot check's file.</summary>
    public static double CoresWhileModelRuns(string model)
    {
        var path = Path.Combine(Lab.Runs, $"spot-{model.Replace(':', '-')}.jsonl");
        if (!File.Exists(path)) return double.NaN;
        var rows = File.ReadLines(path).Select(l => JsonSerializer.Deserialize<SpotRow>(l, Json)!).ToList();
        return rows.Sum(r => r.ModelCpu) / rows.Sum(r => r.LiveTotal - r.Evidence);
    }

    public static void Report(IReadOnlyList<RunRow> rows, string split)
    {
        Console.WriteLine($"{split}: {rows.Count / 3} questions; times as recorded, quality on Chapter 14's checks (answerable questions)");
        Console.WriteLine($"{"Configuration",-10} {"answer p50",11} {"p95",7} {"facts",6} {"keys",6} {"verified",9} {"escalated",10}");
        foreach (var config in new[] { "3B", "1B", "cascade" })
        {
            var r = rows.Where(x => x.Config == config).ToList();
            var a = r.Where(x => x.Answerable).ToList();
            Console.WriteLine(Invariant($"{config,-10} {Stats.Percentile(r.Where(x => x.Model).Select(x => x.TotalMs / 1000), 0.5),10:F1}s {Stats.Percentile(r.Where(x => x.Model).Select(x => x.TotalMs / 1000), 0.95),6:F1}s {Stats.Mean(a.Select(x => x.FactsCited)),6:F2} {Stats.Mean(a.Select(x => x.KeyValues)),6:F2} {Stats.Mean(r.Select(x => x.Verified)),9:F2} {r.Count(x => x.Escalated),10}"));
        }
    }

    /// <summary>
    /// A closed-loop load test of the live pipeline (nothing replayed): workers send the dev questions that reach the
    /// model, in order, one after another, for a fixed time at each concurrency.
    /// </summary>
    public static async Task LoadAsync(Lab lab, int[] levels, TimeSpan duration)
    {
        var generator = await lab.GeneratorAsync(Lab.LiveChat(Lab.Large));
        var recorded = await lab.GeneratorAsync(Lab.AnswerChat(Lab.Large));
        var questions = new List<GoldenQuestion>();
        foreach (var q in lab.Split("dev"))
        {
            if ((await Lab.AnswerAsync(recorded, q)).Timings.PromptTokens > 0) questions.Add(q);
        }

        await Lab.AnswerAsync(generator, questions[0]);
        Console.WriteLine($"Closed-loop load, llama3.2 3B live on the CPU, {duration.TotalSeconds:F0} s per level");
        Console.WriteLine($"{"workers",8} {"answers",8} {"per hour",9} {"p50 s",7} {"p95 s",7}");
        var offset = 1;
        var written = new List<string>();
        foreach (var level in levels)
        {
            var start = offset;
            var result = await ClosedLoopLoad.RunAsync(level, duration, (i, ct) => Lab.AnswerAsync(generator, questions[(start + i) % questions.Count]));
            offset += result.Completed;
            Console.WriteLine(Invariant($"{level,8} {result.Completed,8} {result.PerHour,9:F0} {result.Percentile(0.5),7:F1} {result.Percentile(0.95),7:F1}"));
            written.Add(JsonSerializer.Serialize(new LoadLevel(level, result.Completed, result.Seconds, result.PerHour, result.Percentile(0.5), result.Percentile(0.95)), Json));
        }

        await File.WriteAllLinesAsync(Path.Combine(Lab.Runs, "load.jsonl"), written);
    }
}
