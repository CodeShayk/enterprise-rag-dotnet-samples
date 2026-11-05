using System.Diagnostics;

namespace Contoso.Knowledge.Agents;

/// <summary>
/// One step of a request: a model call with its tokens, or a tool call with its arguments and what came back. Kind is
/// "model", "tool", "denied" (the policy refused the call), "approval" (it waits for a person) or "budget" (a limit
/// stopped the loop).
/// </summary>
public sealed record TrajectoryStep(int Index, string Kind, string Name, string Detail, double Milliseconds,
    long PromptTokens = 0, long OutputTokens = 0, string Outcome = "");

/// <summary>Every step one request took, in order: what an evaluation reads, and what Chapter 20's spans show as they happen.</summary>
public sealed class TrajectoryLog
{
    private readonly List<TrajectoryStep> _steps = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<TrajectoryStep> Steps => _steps;

    public TrajectoryStep Add(string kind, string name, string detail, double milliseconds, long promptTokens = 0, long outputTokens = 0, string outcome = "")
    {
        lock (_lock)
        {
            var step = new TrajectoryStep(_steps.Count + 1, kind, name, detail, milliseconds, promptTokens, outputTokens, outcome);
            _steps.Add(step);

            // Model and tool calls have spans of their own (Chapter 20); a refusal, an approval or a budget stop is an
            // event on the span it happened in.
            if (kind is not ("model" or "tool"))
            {
                Activity.Current?.AddEvent(new ActivityEvent($"agent.{kind}", tags: new ActivityTagsCollection { ["agent.step.name"] = name, ["agent.step.outcome"] = outcome }));
            }

            return step;
        }
    }

    public int ModelCalls => _steps.Count(s => s.Kind == "model");

    public long PromptTokens => _steps.Sum(s => s.PromptTokens);

    public double Seconds => _steps.Sum(s => s.Milliseconds) / 1000;

    /// <summary>The tools that ran, in the order they first ran.</summary>
    public IReadOnlyList<string> ToolsCalled => [.. _steps.Where(s => s.Kind == "tool").Select(s => s.Name).Distinct()];
}
