using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Generation;
using Microsoft.Agents.AI.Workflows;

namespace Ch16.Corrective.Cli;

/// <summary>
/// The corrective loop as a Microsoft Agent Framework workflow: one executor per state, and an edge for every
/// transition the state machine can take, with a condition on the state the step returned. The steps are the state
/// machine's own; the graph is its transition table, which the framework can draw, checkpoint and trace.
/// </summary>
public static class CragWorkflow
{
    /// <summary>The message every executor passes on: the request and the state its step chose next.</summary>
    public sealed record Step(CragRun Run, CragState Next);

    public static readonly (CragState From, CragState To)[] Transitions =
    [
        (CragState.Retrieve, CragState.Grade), (CragState.Retrieve, CragState.Generate), (CragState.Retrieve, CragState.Abstain),
        (CragState.Grade, CragState.Generate), (CragState.Grade, CragState.Correct), (CragState.Grade, CragState.Abstain),
        (CragState.Correct, CragState.Grade),
        (CragState.Generate, CragState.Verify), (CragState.Generate, CragState.Done),
        (CragState.Verify, CragState.Regenerate), (CragState.Verify, CragState.Abstain), (CragState.Verify, CragState.Done),
        (CragState.Regenerate, CragState.Verify), (CragState.Regenerate, CragState.Done),
        (CragState.Abstain, CragState.Done),
    ];

    public static Workflow Build(CorrectiveRag loop)
    {
        var steps = new Dictionary<CragState, ExecutorBinding>
        {
            [CragState.Retrieve] = Bind("retrieve", (run, ct) => loop.RetrieveAsync(run, ct)),
            [CragState.Grade] = Bind("grade", (run, ct) => loop.GradeAsync(run, ct)),
            [CragState.Correct] = Bind("correct", (run, ct) => loop.CorrectAsync(run, ct)),
            [CragState.Generate] = Bind("generate", (run, ct) => loop.GenerateAsync(run, ct)),
            [CragState.Verify] = Bind("verify", (run, _) => Task.FromResult(loop.Verify(run))),
            [CragState.Regenerate] = Bind("regenerate", (run, ct) => loop.RegenerateAsync(run, ct)),
            [CragState.Abstain] = Bind("abstain", (run, ct) => loop.AbstainAsync(run, ct)),
            // A function executor may yield only what it returns: Done returns the answer, and the framework yields it.
            [CragState.Done] = ((Func<Step, IWorkflowContext, CancellationToken, ValueTask<Answer>>)((step, _, _) => ValueTask.FromResult(step.Run.Answer!))).BindAsExecutor("done"),
        };

        var builder = new WorkflowBuilder(steps[CragState.Retrieve]);
        foreach (var (from, to) in Transitions) builder.AddEdge<Step>(steps[from], steps[to], step => step?.Next == to);
        return builder.WithOutputFrom(steps[CragState.Done]).Build();
    }

    public static async Task<Answer> RunAsync(Workflow workflow, CragRun run, CancellationToken cancellationToken = default)
    {
        await using var execution = await InProcessExecution.RunAsync(workflow, new Step(run, CragState.Retrieve), cancellationToken: cancellationToken);
        return execution.OutgoingEvents.OfType<WorkflowOutputEvent>().Select(e => e.As<Answer>()).LastOrDefault()
            ?? throw new InvalidOperationException($"The workflow yielded no answer. Its events: {string.Join("; ", execution.OutgoingEvents)}");
    }

    private static ExecutorBinding Bind(string id, Func<CragRun, CancellationToken, Task<CragState>> step) =>
        ((Func<Step, IWorkflowContext, CancellationToken, ValueTask<Step>>)(async (message, _, ct) => message with { Next = await step(message.Run, ct) })).BindAsExecutor(id);
}
