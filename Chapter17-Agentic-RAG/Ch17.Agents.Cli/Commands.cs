using System.Globalization;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Agents;
using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Ch17.Agents.Cli;

public static class Commands
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The records as each person sees them, every task's reference query, and statements the guard refuses.</summary>
    public static void Records(Lab lab)
    {
        foreach (var person in new[] { People.Nadia, People.Kwame, People.Grace })
        {
            using var session = lab.Records.Open(person);
            var rows = session.Views.Contains("work_orders") ? session.QueryTrusted("SELECT count(*) FROM work_orders").Rows[0][0] : "no view";
            Console.WriteLine($"{person.Name,-14} views: {string.Join(", ", session.Views)}; work orders visible: {rows}");
        }

        Console.WriteLine();
        foreach (var task in AgentTask.Load(Lab.TaskFile).Where(t => t.ReferenceSql is not null))
        {
            using var session = lab.Records.Open(task.Person);
            var result = session.Query(task.ReferenceSql!);
            Console.WriteLine($"{task.Id} {task.Asker,-13} {string.Join(" | ", result.Rows.Select(r => string.Join(", ", r)))}");
        }

        Console.WriteLine();
        using var nadia = lab.Records.Open(People.Nadia);
        string[] attempts =
        [
            "SELECT count(*) FROM work_orders WHERE asset_tag = 'P-201'",
            "DELETE FROM work_orders WHERE status = 'open'",
            "SELECT 1; DROP TABLE assets",
            "SELECT * FROM cmms.work_orders WHERE asset_tag = 'GBX-07'",
            "SELECT * FROM read_csv('corpus/sources/database/work-orders.csv')",
            "SELECT * FROM information_schema.tables",
            "SELECT * FROM work_orders WHERE asset_tag = “P-201”",
            "WITH late AS (SELECT * FROM work_orders WHERE overdue) SELECT count(*) FROM late",
        ];
        foreach (var sql in attempts)
        {
            var verdict = nadia.Check(sql);
            Console.WriteLine($"{(verdict.Allowed ? "runs   " : "refused")}  {sql}{(verdict.Allowed ? "" : $"\n         {verdict.Reason}")}");
        }
    }

    /// <summary>
    /// Text-to-SQL against the reference queries: each records question written as SQL by llama3.2 under four set-ups,
    /// scored by execution accuracy (the same rows as the reference query, extra columns allowed).
    /// </summary>
    public static async Task SqlAsync(Lab lab, Configurations configurations, IReadOnlyList<AgentTask> tasks)
    {
        var setups = new (string Name, bool Semantic, bool Subset, bool Examples, bool Repair)[]
        {
            ("raw schema", false, false, false, false),
            ("+ definitions", true, false, false, false),
            ("+ subset, examples", true, true, true, false),
            ("+ one repair", true, true, true, true),
        };
        var withSql = tasks.Where(t => t.ReferenceSql is not null).ToList();
        Console.WriteLine($"{withSql.Count} records questions ({lab.Split})\n");
        Console.WriteLine($"{"Set-up",-20} {"Right",6} {"Ran",5} {"Refused",8} {"Failed",7} {"Tokens",7} {"p50 s",6}");
        foreach (var setup in setups)
        {
            var (right, ran, refused, failed, tokens, seconds) = (0, 0, 0, 0, new List<int>(), new List<double>());
            foreach (var task in withSql)
            {
                var question = task.RecordsQuestion ?? task.Question;
                using var session = lab.Records.Open(task.Person, setup.Semantic);
                using var reference = lab.Records.Open(task.Person);
                var tables = setup.Subset ? await configurations.Selector.SelectAsync(question, session.Views) : session.Views;
                var attempt = await configurations.TextToSql.RunAsync(question, session, tables, setup.Examples, setup.Repair);
                var ok = Same(reference.Query(task.ReferenceSql!), attempt.Result);
                (right, ran, refused, failed) = (right + (ok ? 1 : 0), ran + (attempt.Ran ? 1 : 0), refused + (attempt.Rejected is null ? 0 : 1), failed + (attempt.Failed is null ? 0 : 1));
                tokens.Add(attempt.PromptTokens);
                seconds.Add(attempt.Milliseconds / 1000);
                if (Environment.GetEnvironmentVariable("CH17_SQL_DETAIL") == "1")
                {
                    Console.WriteLine($"  {task.Id} {(ok ? "right" : "WRONG")} {attempt.Sql.ReplaceLineEndings(" ")}{(attempt.Rejected ?? attempt.Failed) switch { null => "", var e => $"  [{e}]" }}");
                }
            }

            Console.WriteLine($"{setup.Name,-20} {right,3}/{withSql.Count,-2} {ran,5} {refused,8} {failed,7} {Median(tokens),7:F0} {Median(seconds),6:F1}");
        }
    }

    /// <summary>The same rows as the reference: as many rows, each reference row's values found in its own result row.</summary>
    public static bool Same(QueryResult reference, QueryResult? candidate)
    {
        if (candidate is null || candidate.Rows.Count != reference.Rows.Count) return false;
        var remaining = candidate.Rows.Select(r => r.Select(Normalize).ToList()).ToList();
        foreach (var values in reference.Rows.Select(r => r.Select(Normalize).ToList()))
        {
            var match = remaining.FirstOrDefault(r => values.All(r.Contains));
            if (match is null) return false;
            remaining.Remove(match);
        }

        return true;
    }

    private static string Normalize(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d.ToString("0.####", CultureInfo.InvariantCulture) : value.Trim().ToLowerInvariant();

    public static async Task RunAsync(Lab lab, Configurations configurations, string config, IReadOnlyList<AgentTask> tasks)
    {
        var path = Path.Combine(Lab.Runs, "answers", $"{config}-{lab.Split}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var kept = File.Exists(path) ? File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<TaskOutcome>(l)!).Where(o => tasks.All(t => t.Id != o.Id)).ToList() : [];
        var outcomes = new List<TaskOutcome>(kept);
        foreach (var task in tasks)
        {
            var outcome = await configurations.RunAsync(config, task);
            outcomes.Add(outcome);
            var score = Scoring.Score(task, outcome);
            Console.WriteLine($"{task.Id} {task.Kind,-9} {(score.Correct ? "right" : "WRONG")} {outcome.ModelCalls} calls {outcome.Seconds,5:F1} s  [{string.Join(", ", outcome.Steps.Where(s => s.Kind is "tool" or "denied" or "budget").Select(s => s.Name))}]  {One(outcome.Text, 140)}");
            await File.WriteAllLinesAsync(path, outcomes.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => JsonSerializer.Serialize(o, Json)));
        }

        Console.WriteLine($"\nrecorded: {configurations.ToolChat.Recorded} tool turns, {configurations.Answers.Recorded} answers; replayed {configurations.ToolChat.Replayed} and {configurations.Answers.Replayed}");
    }

    public static IReadOnlyDictionary<string, TaskOutcome> Outcomes(string config, string split)
    {
        var path = Path.Combine(Lab.Runs, "answers", $"{config}-{split}.jsonl");
        return File.Exists(path) ? File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<TaskOutcome>(l)!).ToDictionary(o => o.Id) : new Dictionary<string, TaskOutcome>();
    }

    /// <summary>Each configuration's recorded outcomes scored by kind of task, with its cost, and against the first one.</summary>
    public static void Compare(Lab lab, IReadOnlyList<string> configs)
    {
        string[] kinds = ["documents", "records", "mixed", "guarded"];
        Console.WriteLine($"Agent tasks v1, {lab.Split}: {string.Join(", ", kinds.Select(k => $"{lab.Tasks.Count(t => t.Kind == k)} {k}"))}\n");
        Console.WriteLine($"{"Configuration",-13} {string.Join(" ", kinds.Select(k => $"{k,9}"))} {"Plan",6} {"Calls",6} {"p50 s",6} {"p95 s",6}");
        var scores = new Dictionary<string, Dictionary<string, TaskScore>>();
        var footnotes = new List<string>();
        foreach (var config in configs)
        {
            var outcomes = Outcomes(config, lab.Split);
            var scored = lab.Tasks.Where(t => outcomes.ContainsKey(t.Id)).Select(t => Scoring.Score(t, outcomes[t.Id])).ToList();
            if (scored.Count == 0) continue;
            scores[config] = scored.ToDictionary(s => s.Id);
            var byKind = kinds.Select(k => scored.Where(s => s.Kind == k).ToList()).Select(g => g.Count == 0 ? "-" : $"{g.Count(s => s.Correct)}/{g.Count}");
            var planned = scored.Where(s => lab.Tasks.First(t => t.Id == s.Id).Tools.Count > 0).ToList();
            var seconds = scored.Select(s => s.Seconds).Order().ToList();
            Console.WriteLine($"{config,-13} {string.Join(" ", byKind.Select(v => $"{v,9}"))} {$"{planned.Count(s => s.PlanRight)}/{planned.Count}",6} {scored.Average(s => s.Calls),6:F2} {Percentile(seconds, 0.5),6:F1} {Percentile(seconds, 0.95),6:F1}");
            var rowClaims = outcomes.Values.Where(o => lab.Tasks.Any(t => t.Id == o.Id)).ToList();
            if (rowClaims.Sum(o => o.RowClaims) > 0) footnotes.Add($"{config}: {rowClaims.Sum(o => o.RowClaims)} claims cite query rows, {rowClaims.Sum(o => o.RowClaimsFailed)} fail the row check");
        }

        foreach (var note in footnotes) Console.WriteLine(note);
        if (scores.Count < 2) return;
        var baseline = configs.First(scores.ContainsKey);
        Console.WriteLine($"\nRight answers against {baseline} (paired bootstrap, 10,000 resamples, seed 2027):");
        foreach (var config in configs.Where(c => c != baseline && scores.ContainsKey(c)))
        {
            var ids = scores[baseline].Keys.Intersect(scores[config].Keys).Order().ToList();
            var d = Bootstrap.Paired([.. ids.Select(id => scores[baseline][id].Correct ? 1.0 : 0)], [.. ids.Select(id => scores[config][id].Correct ? 1.0 : 0)]);
            Console.WriteLine($"  {config,-11} {Signed(d.Mean)} [{Signed(d.Low)}, {Signed(d.High)}]");
        }
    }

    /// <summary>One task's steps as the trajectory log recorded them, then the answer and its sources.</summary>
    public static async Task TraceAsync(Lab lab, Configurations configurations, string id, string config)
    {
        var task = AgentTask.Load(Lab.TaskFile).Single(t => t.Id == id);
        var outcome = await configurations.RunAsync(config, task);
        Console.WriteLine($"{task.Id} ({task.Asker}): {task.Question}\n");
        foreach (var step in outcome.Steps)
        {
            var tokens = step.PromptTokens == 0 ? "" : step.OutputTokens == 0 ? $"  {step.PromptTokens} tokens in" : $"  {step.PromptTokens} in / {step.OutputTokens} out";
            Console.WriteLine($"{step.Index}. {step.Kind,-6} {step.Name,-17} {step.Milliseconds / 1000,6:F2} s{tokens}\n   {step.Detail.ReplaceLineEndings(" ")}");
            var result = step.Outcome.ReplaceLineEndings(" | ");
            if (result.Length > 0) Console.WriteLine($"   -> {result[..Math.Min(260, result.Length)]}");
        }

        Console.WriteLine($"\nAnswer ({outcome.Status}, {outcome.ModelCalls} model calls, {outcome.PromptTokens} prompt tokens, {outcome.Seconds:F1} s):\n{outcome.Text}");
        foreach (var source in outcome.Sources) Console.WriteLine($"  [{source.Id}] {source.Document}: {source.Title}");
    }

    /// <summary>The guardrails one at a time: the allow-list, a write that waits for approval and a step budget.</summary>
    public static async Task GuardAsync(Lab lab, Configurations configurations)
    {
        using (var grace = lab.Context(People.Grace))
        {
            Console.WriteLine($"Grace's tools: {string.Join(", ", lab.Tools(grace).Create().Select(t => t.Name))}");
        }

        using (var tomas = lab.Context(People.Tomas))
        {
            var question = "P-202's seal is dripping again. Draft a P2 work order to change it to PN-4471-C.";
            var outcome = await configurations.Agent().RunAsync(question, tomas, lab.Tools(tomas).Create());
            Console.WriteLine($"\nTomás: {question}");
            foreach (var step in tomas.Trajectory.Steps) Console.WriteLine($"  {step.Kind,-8} {step.Name,-17} {step.Detail.ReplaceLineEndings(" ")}{(step.Outcome.Length > 0 ? $" -> {One(step.Outcome, 120)}" : "")}");
            Console.WriteLine($"  drafts written: {tomas.Drafts.Count}; waiting for approval: {outcome.Approvals.Count}");
            foreach (var request in outcome.Approvals)
            {
                Console.WriteLine($"  approval request {request.RequestId}: {(request.ToolCall as FunctionCallContent)?.Name}({One(ToolAgent.Arguments((request.ToolCall as FunctionCallContent)?.Arguments), 200)})");
            }
        }

        using (var nadia = lab.Context(People.Nadia))
        {
            var tight = new ToolAgent(configurations.ToolChat, lab.Generator(configurations.Answers), Lab.O200k, new AgentBudget(MaxModelCalls: 1));
            var task = AgentTask.Load(Lab.TaskFile).Single(t => t.Id == "A12");
            var outcome = await tight.RunAsync(task.Question, nadia, lab.Tools(nadia).Create());
            Console.WriteLine($"\nNadia, with a budget of one model call: {task.Question}");
            foreach (var step in nadia.Trajectory.Steps) Console.WriteLine($"  {step.Kind,-8} {step.Name,-17} {One(step.Detail, 120)}");
            Console.WriteLine($"  stopped by: {outcome.Exhausted ?? "nothing"}; answer: {outcome.Answer.Text.ReplaceLineEndings(" ")}");
        }
    }

    /// <summary>The MCP server as different people see it: its tools, the same search as two people, no token, and the cost of the hop.</summary>
    public static async Task McpAsync(Lab lab)
    {
        foreach (var (person, query) in new[] { ("nadia.rahman", "service interval P-201"), ("riley.chen", "Fabrikam Seals contract delivery times"), ("grace.liu", "Fabrikam Seals contract delivery times") })
        {
            await using var client = await McpSearch.ConnectAsync(McpSearch.Token(person) ?? throw new InvalidOperationException($"No token for {person}."));
            var tool = (await client.ListToolsAsync()).Single(t => t.Name == ContosoTools.SearchDocuments);
            var result = await client.CallToolAsync(tool.Name, new Dictionary<string, object?> { ["query"] = query });
            var sources = result.StructuredContent is { } content && content.TryGetProperty("sources", out var found)
                ? found.EnumerateArray().Select(s => $"{s.GetProperty("documentId")}: {s.GetProperty("title")}").ToList() : [];
            Console.WriteLine($"{person,-13} {tool.Name} (read-only: {tool.ProtocolTool.Annotations?.ReadOnlyHint}, description {ToolReview.Hash(tool.Description)}) \"{query}\"");
            foreach (var source in sources) Console.WriteLine($"   {source}");
        }

        try
        {
            await using var anonymous = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = McpSearch.Endpoint, TransportMode = HttpTransportMode.StreamableHttp }));
            Console.WriteLine("no token: connected (unexpected)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"no token: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
        }

        // The same 20 searches in process and through the server, after one warm-up each.
        var queries = lab.Tasks.Select(t => t.Question).Take(20).ToList();
        using var context = lab.Context(People.Nadia);
        var local = lab.Tools(context).Create().OfType<AIFunction>().Single(t => t.Name == ContosoTools.SearchDocuments);
        await using var remote = await McpSearch.ConnectAsync(McpSearch.Token("nadia.rahman")!);
        var remoteTool = (await McpSearch.ToolsAsync(remote, context)).OfType<AIFunction>().Single();
        foreach (var (name, function) in new[] { ("in process", local), ("through MCP", remoteTool) })
        {
            await function.InvokeAsync(new AIFunctionArguments { ["query"] = queries[0] });
            var times = new List<double>();
            foreach (var query in queries)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await function.InvokeAsync(new AIFunctionArguments { ["query"] = query });
                times.Add(clock.Elapsed.TotalMilliseconds);
            }

            Console.WriteLine($"{name,-12} {queries.Count} searches: p50 {Percentile([.. times.Order()], 0.5):F0} ms, p95 {Percentile([.. times.Order()], 0.95):F0} ms");
        }
    }

    /// <summary>Text on one line, whitespace collapsed, cut to a length for the console.</summary>
    private static string One(string text, int max)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= max ? line : line[..max];
    }

    private static double Median(IReadOnlyList<int> values) => values.Count == 0 ? 0 : values.Order().ElementAt(values.Count / 2);

    private static double Median(IReadOnlyList<double> values) => Percentile([.. values.Order()], 0.5);

    private static double Percentile(IReadOnlyList<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    private static string Signed(double x) => (Math.Round(x, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
}
