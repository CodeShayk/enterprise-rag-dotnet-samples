using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Observability;
using Microsoft.Extensions.Configuration;

namespace Ch20.Observability.Cli;

public static class Commands
{
    /// <summary>
    /// The pseudonym key is a secret: <c>dotnet user-secrets set Telemetry:PseudonymKey &lt;value&gt;</c>. Without one, each
    /// run makes its own, and pseudonyms don't link across runs. Production keeps it in a secret store and rotates it.
    /// </summary>
    private static readonly byte[] Key =
        new ConfigurationBuilder().AddUserSecrets(typeof(Commands).Assembly).Build()["Telemetry:PseudonymKey"] is { Length: > 0 } secret
            ? Encoding.UTF8.GetBytes(secret)
            : RandomNumberGenerator.GetBytes(32);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web);

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>
    /// One question in its own trace: a server span for the request, with the asker's tenant and a pseudonym as baggage
    /// that every span below it carries; the question's text only if the capture mode allows it.
    /// </summary>
    public static async Task<(Answer Answer, IReadOnlyList<Activity> Spans)> AskAsync(Telemetry telemetry, GroundedAnswerGenerator generator, GoldenQuestion q, ContentCapture capture = ContentCapture.Off)
    {
        var asker = People.Find(q.Asker)!;
        var before = telemetry.Spans.Count;
        Answer answer;
        using (var request = RagTelemetry.Source.StartActivity("ask", ActivityKind.Server))
        {
            request?.AddBaggage("contoso.tenant", asker.Tenant);
            request?.AddBaggage("user.pseudonym", TelemetryContent.Pseudonym(asker.Id, Key));
            request?.SetTag("contoso.tenant", asker.Tenant);
            request?.SetTag("user.pseudonym", TelemetryContent.Pseudonym(asker.Id, Key));
            request?.SetTag("rag.question.sha", Sha(q.Question));
            if (TelemetryContent.Text(q.Question, capture) is { } text) request?.SetTag("rag.question", text);
            answer = await generator.AnswerAsync(q.Question, asker);
            RagTelemetry.RecordAnswer(answer, asker.Tenant, answer.Timings.TotalMs / 1000);
        }

        return (answer, telemetry.Spans.Skip(before).ToList());
    }

    /// <summary>"Why this answer?": one golden question's trace as a tree, each span with its duration and what it recorded.</summary>
    public static async Task WhyAsync(Lab lab, string id)
    {
        using var telemetry = new Telemetry();
        var generator = await lab.GeneratorAsync(Lab.AnswerChat());
        var q = lab.Questions.Single(x => x.Id == id);
        var (answer, spans) = await AskAsync(telemetry, generator, q);
        Console.WriteLine($"{q.Id} ({People.Find(q.Asker)!.Name}): {q.Question}\n{answer.Text}\n");
        var root = spans.Single(s => s.OperationName == "ask");
        Print(root, spans, "");
        Console.WriteLine($"\nModel time as recorded: first token {answer.Timings.FirstTokenMs / 1000:F1} s, answer {answer.Timings.TotalMs / 1000:F1} s; replayed, the model span takes milliseconds.");
    }

    /// <summary>The attributes the view prints, in this order; a backend shows them all.</summary>
    private static readonly string[] Shown =
    [
        "contoso.tenant", "user.pseudonym", "rag.answer.status", "rag.answer.confidence", "rag.prompt.version", "rag.hits", "rag.hit.ids", "rag.hit.scores",
        "rag.candidates", "rag.evidence.blocks", "rag.evidence.tokens", "rag.evidence.best_probability", "rag.compress.tokens_in", "rag.compress.tokens_out",
        "gen_ai.request.model", "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens", "rag.claims", "rag.claims.valid",
    ];

    internal static void Print(Activity span, IReadOnlyList<Activity> spans, string indent)
    {
        var tags = Shown.Where(k => span.GetTagItem(k) is not null && (indent.Length == 0 || k is not ("contoso.tenant" or "user.pseudonym")))
            .Select(k => $"{k.Replace("rag.", "", StringComparison.Ordinal).Replace("gen_ai.", "", StringComparison.Ordinal)}={Format(span.GetTagItem(k))}");
        Console.WriteLine(Invariant($"{indent}{span.OperationName}  {span.Duration.TotalMilliseconds:F1} ms  {string.Join("  ", tags)}"));
        foreach (var child in spans.Where(s => s.ParentSpanId == span.SpanId).OrderBy(s => s.StartTimeUtc)) Print(child, spans, indent + "  ");
    }

    private static string Format(object? value) => value switch
    {
        string[] a => "[" + string.Join(", ", a.Take(2)) + (a.Length > 2 ? $", … {a.Length}" : "") + "]",
        double[] d => "[" + string.Join(", ", d.Take(2).Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))) + (d.Length > 2 ? ", …" : "") + "]",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    public static string RecordsFile(string split) => Path.Combine(Lab.Runs, $"records.{split}.jsonl");

    /// <summary>
    /// Every question of a split through the traced pipeline: an answer record per trace, the time each kind of span
    /// took, the quality metrics the meter counted, and whether each answer matches the one Chapter 14 shipped.
    /// </summary>
    public static async Task RecordAsync(Lab lab, string split)
    {
        using var telemetry = new Telemetry();
        var generator = await lab.GeneratorAsync(Lab.AnswerChat());
        var shipped = File.ReadLines(Path.Combine(Lab.Runs, "..", "ch14", "answers", $"compressed-{split}.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("answer").GetProperty("text").GetString()!);
        Directory.CreateDirectory(Lab.Runs);
        await using var writer = new StreamWriter(RecordsFile(split));
        var (questions, reproduced) = (lab.Questions.Where(q => q.Split == split).ToList(), 0);
        foreach (var q in questions)
        {
            var (answer, spans) = await AskAsync(telemetry, generator, q);
            var asker = People.Find(q.Asker)!;
            var record = AnswerRecord.FromTrace(spans, q.Id, Sha(q.Question), null, TelemetryContent.Pseudonym(asker.Id, Key), asker.Tenant);
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, Json));
            reproduced += shipped.TryGetValue(q.Id, out var text) && text == answer.Text ? 1 : 0;
        }

        Console.WriteLine($"{questions.Count} {split} questions traced: {telemetry.Spans.Count} spans in {telemetry.Spans.Select(s => s.TraceId).Distinct().Count()} traces; answers identical to Chapter 14's: {reproduced}/{questions.Count}\n");
        Console.WriteLine($"{"Span",-36} {"count",6} {"median ms",10} {"p95 ms",8}");
        foreach (var g in telemetry.Spans.GroupBy(s => s.OperationName).OrderBy(g => g.Min(s => s.StartTimeUtc.Ticks)))
        {
            var ms = g.Select(s => s.Duration.TotalMilliseconds).Order().ToList();
            Console.WriteLine($"{g.Key,-36} {ms.Count,6} {ms[ms.Count / 2],10:F1} {ms[(int)Math.Ceiling(0.95 * ms.Count) - 1],8:F1}");
        }

        long Count(string metric, string? key = null, string? value = null) => telemetry.Sum(metric, t => key is null || Equals(t.GetValueOrDefault(key), value));
        var answers = Count("rag.answers");
        var (valid, invalid, uncited) = (Count("rag.claims", "rag.claim.outcome", "valid"), Count("rag.claims", "rag.claim.outcome", "invalid"), Count("rag.claims", "rag.claim.outcome", "uncited"));
        Console.WriteLine($"\nQuality telemetry, {answers} answers:");
        Console.WriteLine($"  answered {Count("rag.answers", "rag.answer.status", "answered")}, partial {Count("rag.answers", "rag.answer.status", "partial")}, not found {Count("rag.answers", "rag.answer.status", "not_found")}, clarify {Count("rag.answers", "rag.answer.status", "clarify")}");
        Console.WriteLine($"  empty retrieval {Count("rag.retrieval.empty", "rag.retriever", "hybrid, re-ranked")}; weak evidence (best block below 0.1) {Count("rag.evidence.weak")}");
        Console.WriteLine(Invariant($"  claims {valid + invalid + uncited}: valid {valid}, invalid {invalid}, uncited {uncited}; citation coverage {valid / (double)Math.Max(1, valid + invalid):P0}"));
        Console.WriteLine($"  answer-check findings {Count("rag.checks")}");
    }

    public static List<AnswerRecord> Records(string split) =>
        [.. File.ReadLines(RecordsFile(split)).Select(l => JsonSerializer.Deserialize<AnswerRecord>(l, Json)!)];

    /// <summary>
    /// Root cause from traces alone: Chapter 15's 34 labeled failures on dev, each placed by its record and the golden
    /// spans, against where the labels put it.
    /// </summary>
    public static void Diagnose(Lab lab)
    {
        var records = Records("dev").ToDictionary(r => r.QuestionId);
        var cases = File.ReadLines(Path.Combine(Lab.Runs, "..", "ch15", "failure-cases.dev.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var rows = new List<(string Label, string Trace)>();
        foreach (var c in cases)
        {
            var (id, kind, stage) = (c.GetProperty("id").GetString()!, c.GetProperty("kind").GetString()!, c.GetProperty("stage").GetString()!);
            var record = records[id];
            var q = lab.Questions.Single(x => x.Id == id);
            var label = stage.Contains("FP2") ? "retrieval" : stage.Contains("FP3") ? "assembly or compression" : kind == "wrong answer" ? "generation"
                : stage.Contains("wrong citation") || stage.Contains("isn't in its source") ? "citation" : "generation";
            var trace = kind == "wrong answer"
                ? FailurePoints.ForAnswer(record, [.. lab.Spans(q).Select(s => (s.Doc, s.Start, s.End, s.Fact))])
                : FailurePoints.ForClaim(record, c.GetProperty("claim").GetInt32());
            rows.Add(($"{kind}: {label}", trace));
        }

        Console.WriteLine($"{"Labeled (Chapter 15)",-44} {"n",3}  {"the trace says"}");
        foreach (var g in rows.GroupBy(r => r.Label).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"{g.Key,-44} {g.Count(),3}  {string.Join(", ", g.GroupBy(r => r.Trace).Select(t => $"{t.Key} {t.Count()}"))}");
        }

        var decided = rows.Where(r => r.Trace != "needs a support check").ToList();
        Console.WriteLine($"\nPlaced by the trace alone: {decided.Count} of {rows.Count}; agreeing with the label: {decided.Count(r => r.Label.EndsWith(r.Trace, StringComparison.Ordinal))}");
    }

    /// <summary>
    /// Replay: rerun a recorded answer and compare it stage by stage with its record, then change one stage (here,
    /// compression off) and look at the answer it gives.
    /// </summary>
    public static async Task ReplayAsync(Lab lab, string id, string? expect)
    {
        var record = Records("dev").Single(r => r.QuestionId == id);
        var q = lab.Questions.Single(x => x.Id == id);
        using var telemetry = new Telemetry();
        var (again, spans) = await AskAsync(telemetry, await lab.GeneratorAsync(Lab.AnswerChat()), q);
        var replayed = AnswerRecord.FromTrace(spans, q.Id, Sha(q.Question), null, record.Asker, record.Tenant);
        Console.WriteLine($"{id}: {q.Question}");
        Console.WriteLine($"Record: trace {record.TraceId[..8]}…, {record.Hits.Count} hits, {record.Evidence.Count} evidence segments, prompt {record.PromptVersion}, model {record.Model}, {record.Status}");
        Console.WriteLine($"Replay: hits {(replayed.Hits.SequenceEqual(record.Hits) ? "identical" : "different")}, evidence {(replayed.Evidence.SequenceEqual(record.Evidence) ? "identical" : "different")}, " +
            $"prompt {(replayed.PromptVersion == record.PromptVersion ? "same" : "changed")}, answer {(replayed.Status == record.Status && replayed.ClaimOutcomes.SequenceEqual(record.ClaimOutcomes) ? "identical" : "different")}");
        Console.WriteLine($"  answer: {again.Text}");
        var spansOfQuestion = lab.Spans(q);
        Console.WriteLine($"  failure point from the trace: {FailurePoints.ForAnswer(replayed, [.. spansOfQuestion.Select(s => (s.Doc, s.Start, s.End, s.Fact))])}");

        var (changed, changedSpans) = await AskAsync(telemetry, await lab.GeneratorAsync(Lab.AnswerChat(), compress: false), q);
        var changedRecord = AnswerRecord.FromTrace(changedSpans, q.Id, Sha(q.Question), null, record.Asker, record.Tenant);
        Console.WriteLine($"\nCompression off: {changed.Text}");
        Console.WriteLine($"  evidence segments {changedRecord.Evidence.Count}; failure point from the trace: {FailurePoints.ForAnswer(changedRecord, [.. spansOfQuestion.Select(s => (s.Doc, s.Start, s.End, s.Fact))])}");
        if (expect is not null) Console.WriteLine($"  states \"{expect}\": {(Regex.IsMatch(changed.Text, expect) ? "yes" : "no")}; with compression: {(Regex.IsMatch(again.Text, expect) ? "yes" : "no")}");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
