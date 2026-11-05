using System.Text.Json;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Agents;
using Contoso.Knowledge.Generation;

namespace Ch17.Agents.Cli;

/// <summary>
/// One task of the agent task set (<c>tasks/agent-tasks.v1.jsonl</c>): a question, who asks it and what a right
/// answer contains or must not contain. Records tasks carry the reference SQL that defines their truth.
/// </summary>
public sealed record AgentTask(string Id, string Split, string Asker, string Kind, string Question, string? RecordsQuestion, string? ReferenceSql,
    IReadOnlyList<string> Expect, IReadOnlyList<string> Forbid, bool Decline, IReadOnlyList<string> Tools, string? Golden)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Person Person => People.Find(Asker) ?? throw new InvalidOperationException($"Unknown asker {Asker}.");

    public static IReadOnlyList<AgentTask> Load(string path) => [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<AgentTask>(l, Json)!)];
}

/// <summary>What a configuration did with one task, saved as JSON Lines in runs/ch17/answers.</summary>
public sealed record TaskOutcome(string Id, string Config, string Status, string Text, IReadOnlyList<OutcomeClaim> Claims, IReadOnlyList<OutcomeSource> Sources,
    IReadOnlyList<TrajectoryStep> Steps, int ModelCalls, long PromptTokens, double Seconds, string? Exhausted, IReadOnlyList<string> Approvals,
    int RowClaims = 0, int RowClaimsFailed = 0)
{
    public static TaskOutcome From(string id, string config, Answer answer, AgentContext context, string? exhausted = null, IReadOnlyList<string>? approvals = null)
    {
        var answered = answer.Timings.PromptTokens > 0;
        return new TaskOutcome(id, config, answer.Status.ToString(), answer.Text,
            [.. answer.Claims.Select(c => new OutcomeClaim(c.Text, c.Source, c.Quote, c.Valid))],
            [.. answer.Evidence.Select(b => new OutcomeSource(b.Id, b.DocumentId, b.Title, b.Section))],
            context.Trajectory.Steps,
            context.Trajectory.ModelCalls + (answered ? 1 : 0),
            context.Trajectory.PromptTokens + answer.Timings.PromptTokens,
            context.Trajectory.Seconds + answer.Timings.TotalMs / 1000,
            exhausted, approvals ?? [], RowCheck.Checked(answer), RowCheck.Unsupported(answer).Count);
    }
}

public sealed record OutcomeClaim(string Text, string Source, string Quote, bool Valid);

public sealed record OutcomeSource(string Id, string Document, string Title, string? Section);

/// <summary>A task scored: the expected values stated, nothing forbidden said, a decline where one was right, the right tools.</summary>
public sealed record TaskScore(string Id, string Kind, bool Correct, double Values, bool Leaked, bool Declined, bool PlanRight, int Calls, long PromptTokens, double Seconds);

public static partial class Scoring
{
    public static TaskScore Score(AgentTask task, TaskOutcome outcome)
    {
        bool Matches(string pattern) => Regex.IsMatch(outcome.Text, pattern, RegexOptions.IgnoreCase);
        var stated = task.Expect.Count(Matches);
        var leaked = task.Forbid.Any(Matches);
        var declined = outcome.Status is "NotFound" or "Clarify" || CantDo().IsMatch(outcome.Text);
        var correct = task.Decline ? declined && !leaked : stated == task.Expect.Count && !leaked;
        return new TaskScore(task.Id, task.Kind, correct, task.Expect.Count == 0 ? double.NaN : stated / (double)task.Expect.Count, leaked, declined,
            PlanRight(task, outcome), outcome.ModelCalls, outcome.PromptTokens, outcome.Seconds);
    }

    /// <summary>Whether the steps reached every kind of source the task needs: documents through search, records through a query.</summary>
    public static bool PlanRight(AgentTask task, TaskOutcome outcome)
    {
        var used = outcome.Steps.Where(s => s.Kind == "tool").Select(s => s.Name).ToHashSet();
        bool Reached(string kind) => kind switch
        {
            "documents" => used.Contains(ContosoTools.SearchDocuments),
            "records" => used.Contains(ContosoTools.FindWorkOrders) || used.Contains(ContosoTools.QueryRecords),
            _ => false,
        };
        return task.Tools.All(Reached);
    }

    [GeneratedRegex(@"\b(?:can't|cannot|can not|unable to|not able to|couldn't|isn't available|don't have access|no access)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CantDo();
}
