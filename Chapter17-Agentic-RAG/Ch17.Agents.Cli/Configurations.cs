using System.Diagnostics;
using Contoso.Knowledge.Agents;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Query;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch17.Agents.Cli;

/// <summary>
/// The configurations the chapter compares, from the same parts. The pipeline is Chapter 14's single pass and can't
/// reach the records; the workflow always searches the documents and, when a router says the question is about
/// records, adds one Text-to-SQL step; the agent lets llama3.2 choose its tools; agent-af is the same agent hosted by
/// Microsoft Agent Framework. All of them write the answer with Chapter 14's generator.
/// </summary>
public sealed class Configurations(Lab lab, RecordedChatClient answers, RecordedToolChatClient toolChat, LlmCache cache, TextToSql textToSql,
    TableSelector selector, SemanticRouter router)
{
    public static readonly string[] Names = ["pipeline", "workflow", "agent", "agent-af", "agent-mcp"];

    public RecordedChatClient Answers => answers;

    public RecordedToolChatClient ToolChat => toolChat;

    public LlmCache Cache => cache;

    public TextToSql TextToSql => textToSql;

    public TableSelector Selector => selector;

    public SemanticRouter Router => router;

    /// <summary>Questions the workflow's router compares with: written for it, none from the task set.</summary>
    public static IReadOnlyList<(string Text, string Route)> RouterExamples { get; } =
    [
        ("How many work orders were closed last month?", "records"),
        ("Which work orders are overdue at Northgate?", "records"),
        ("When was the last preventive maintenance on V-114?", "records"),
        ("Who completed the most work orders in December?", "records"),
        ("How many corrective work orders did HG-1102 have this year?", "records"),
        ("Is there an open work order for CNV-12?", "records"),
        ("What is the net price of the PN-3310-A bearing?", "records"),
        ("How many PN-3420-A grease cartridges are in stock?", "records"),
        ("Which parts from Litware Bearings have the longest lead time?", "records"),
        ("Which parts were superseded in 2025?", "records"),
        ("How do I replace the mechanical seal on a CP-200?", "documents"),
        ("What torque applies to the casing bolts?", "documents"),
        ("What does error E-303 mean?", "documents"),
        ("Which oil goes in an HG-400 gearbox?", "documents"),
        ("What does safety bulletin SB-0932 say?", "documents"),
        ("Who must approve a lockout?", "documents"),
        ("How often should the coupling alignment be checked?", "documents"),
        ("What changed in ECN-2240?", "documents"),
        ("Where is pump P-301 installed?", "documents"),
        ("What is the parental leave policy?", "documents"),
    ];

    public static async Task<Configurations> CreateAsync(Lab lab)
    {
        var (passages, queries) = (new OnnxEmbeddingGenerator(lab.Bge), new OnnxEmbeddingGenerator(lab.Bge, queries: true));
        var cache = Lab.Cache();
        return new Configurations(lab, Lab.AnswerChat(), Lab.ToolChat(), cache, new TextToSql(Lab.LiveChat(), cache, Lab.O200k),
            await TableSelector.CreateAsync(passages, queries), await SemanticRouter.CreateAsync(queries, RouterExamples, "documents"));
    }

    public ToolAgent Agent() => new(toolChat, lab.Generator(answers), Lab.O200k);

    public async Task<TaskOutcome> RunAsync(string config, AgentTask task, CancellationToken cancellationToken = default)
    {
        using var context = lab.Context(task.Person);
        switch (config)
        {
            case "pipeline":
                return TaskOutcome.From(task.Id, config, await PipelineAsync(task, context, cancellationToken), context);
            case "workflow":
                return TaskOutcome.From(task.Id, config, await WorkflowAsync(task, context, cancellationToken), context);
            case "agent":
            {
                var outcome = await Agent().RunAsync(task.Question, context, lab.Tools(context).Create(), cancellationToken);
                return TaskOutcome.From(task.Id, config, outcome.Answer, context, outcome.Exhausted, [.. outcome.Approvals.Select(a => (a.ToolCall as FunctionCallContent)?.Name ?? "tool")]);
            }

            case "agent-af":
            {
                var outcome = await AgentFrameworkAsync(task.Question, context, cancellationToken);
                return TaskOutcome.From(task.Id, config, outcome.Answer, context, outcome.Exhausted, [.. outcome.Approvals.Select(a => (a.ToolCall as FunctionCallContent)?.Name ?? "tool")]);
            }

            case "agent-mcp":
            {
                // Search comes from the MCP server, which searches as the asker's token says; the records tools stay local.
                var token = McpSearch.Token(task.Asker) ?? throw new InvalidOperationException($"No MCP token for {task.Asker}: see Ch17.Knowledge.Mcp/Program.cs.");
                await using var client = await McpSearch.ConnectAsync(token, cancellationToken);
                var tools = (await McpSearch.ToolsAsync(client, context, cancellationToken)).Concat(lab.Tools(context).Create().Where(t => t.Name != ContosoTools.SearchDocuments)).ToList();
                var outcome = await Agent().RunAsync(task.Question, context, tools, cancellationToken);
                return TaskOutcome.From(task.Id, config, outcome.Answer, context, outcome.Exhausted, [.. outcome.Approvals.Select(a => (a.ToolCall as FunctionCallContent)?.Name ?? "tool")]);
            }

            default:
                throw new ArgumentException($"Unknown configuration '{config}'.");
        }
    }

    /// <summary>Chapter 14's single pass: retrieval, then one model call. The records aren't reachable.</summary>
    private async Task<Answer> PipelineAsync(AgentTask task, AgentContext context, CancellationToken cancellationToken)
    {
        Answer? answer = null;
        await foreach (var e in lab.Generator(answers).StreamAsync(task.Question, task.Person, cancellationToken: cancellationToken))
        {
            if (e is EvidenceReady ready) context.Ledger.AddPassages(ready.Evidence);
            if (e is AnswerReady done) answer = done.Answer;
        }

        context.Trajectory.Add("tool", ContosoTools.SearchDocuments, task.Question, answer!.Timings.RetrievalMs);
        return answer with { Timings = answer.Timings with { TotalMs = answer.Timings.TotalMs - answer.Timings.RetrievalMs } };
    }

    /// <summary>
    /// The deterministic workflow: search the documents with the question; if the router says records, write SQL for
    /// the question over the tables it needs, run it through the guard (one repair if it fails) and add the rows; answer.
    /// </summary>
    public async Task<Answer> WorkflowAsync(AgentTask task, AgentContext context, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var found = lab.Assembler().Assemble((await lab.Retriever.RetrieveAsync(new(task.Question, task.Person, 4), cancellationToken)).Hits);
        if (!found.IsEmpty) context.Ledger.AddPassages(lab.Compressor().Compress(task.Question, found).Blocks);
        context.Trajectory.Add("tool", ContosoTools.SearchDocuments, task.Question, clock.Elapsed.TotalMilliseconds);

        var route = await router.RouteAsync(task.Question, cancellationToken);
        if (route == "records" && context.Records.Views.Count > 0)
        {
            var tables = await selector.SelectAsync(task.Question, context.Records.Views, cancellationToken: cancellationToken);
            var attempt = await textToSql.RunAsync(task.Question, context.Records, tables, examples: true, repair: true, cancellationToken);
            context.Trajectory.Add("model", "text-to-sql", attempt.Sql, attempt.Milliseconds, attempt.PromptTokens);
            context.Trajectory.Add("tool", ContosoTools.QueryRecords, attempt.Sql, attempt.Result?.Milliseconds ?? 0,
                outcome: attempt.Result?.Render() ?? $"not run: {attempt.Rejected ?? attempt.Failed}");
            if (attempt.Result is { } rows) context.Ledger.AddQuery(rows, "Maintenance records: query");
        }

        Answer? answer = null;
        await foreach (var e in lab.Generator(answers).StreamAsync(task.Question, task.Person, evidence: context.Ledger.Blocks, cancellationToken: cancellationToken))
        {
            if (e is AnswerReady done) answer = done.Answer;
        }

        return answer!;
    }

    /// <summary>The same agent hosted by Microsoft Agent Framework: a ChatClientAgent over the same pipeline, options and tools.</summary>
    public async Task<AgentOutcome> AgentFrameworkAsync(string question, AgentContext context, CancellationToken cancellationToken = default)
    {
        var agent = Agent();
        var meter = new BudgetMeter(agent.Budget.AsLoopBudget());
        AIAgent research = agent.Pipeline(context, meter).AsAIAgent(new ChatClientAgentOptions
        {
            Name = "contoso-research",
            ChatOptions = agent.Options(lab.Tools(context).Create()),
            UseProvidedChatClientAsIs = true,
        });
        var session = await research.CreateSessionAsync(cancellationToken);
        var response = await research.RunAsync(question, session, cancellationToken: cancellationToken);
        return await agent.FinishAsync(question, context, meter, response.Messages, cancellationToken);
    }
}
