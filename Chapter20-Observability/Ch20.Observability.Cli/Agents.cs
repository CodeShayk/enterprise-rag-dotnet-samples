using System.Diagnostics;
using Contoso.Knowledge.Agents;
using Contoso.Knowledge.Observability;
using Microsoft.Extensions.AI;
using static System.FormattableString;
using AgentLab = Ch17.Agents.Cli.Lab;
using AgentConfigurations = Ch17.Agents.Cli.Configurations;

namespace Ch20.Observability.Cli;

/// <summary>Chapter 17's agent under the same telemetry: its model calls and tool calls become spans as they happen.</summary>
public static class Agents
{
    public static async Task TraceAsync(string id)
    {
        using var telemetry = new Telemetry();
        var lab = await AgentLab.LoadAsync("dev");
        var configurations = await AgentConfigurations.CreateAsync(lab);
        var task = lab.Tasks.Single(t => t.Id == id);

        // The function-invocation client opens an execute_tool span per call when the model client below it is traced.
        var model = new ChatClientBuilder(configurations.ToolChat).UseOpenTelemetry(sourceName: RagTelemetry.ModelsSource).Build();
        var answers = new ChatClientBuilder(configurations.Answers).UseOpenTelemetry(sourceName: RagTelemetry.ModelsSource).Build();
        var agent = new ToolAgent(model, lab.Generator(answers), AgentLab.O200k);
        using var context = lab.Context(task.Person);
        AgentOutcome outcome;
        using (var request = RagTelemetry.Source.StartActivity("ask", ActivityKind.Server))
        {
            request?.AddBaggage("contoso.tenant", task.Person.Tenant);
            request?.SetTag("contoso.tenant", task.Person.Tenant);
            outcome = await agent.RunAsync(task.Question, context, lab.Tools(context).Create());
        }

        var spans = telemetry.Spans.ToList();
        Console.WriteLine($"{task.Id} ({task.Person.Name}): {task.Question}\n");
        Commands.Print(spans.Single(s => s.OperationName == "ask"), spans, "");

        var steps = context.Trajectory.Steps;
        Console.WriteLine(Invariant($"\nTrajectory: {steps.Count(s => s.Kind == "model")} model calls and {steps.Count(s => s.Kind == "tool")} tool calls; ") +
            Invariant($"spans: {spans.Count(s => s.OperationName.StartsWith("chat", StringComparison.Ordinal))} chat and {spans.Count(s => s.OperationName.StartsWith("execute_tool", StringComparison.Ordinal))} execute_tool"));
        Console.WriteLine(Invariant($"Model time as recorded: {steps.Where(s => s.Kind == "model").Sum(s => s.Milliseconds) / 1000:F1} s for the research step, {outcome.Answer.Timings.TotalMs / 1000:F1} s for the answer"));
    }
}
