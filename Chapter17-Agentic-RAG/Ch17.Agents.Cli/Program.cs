using System.Text;
using Ch17.Agents.Cli;

// Commands (from the sample-code folder), on the agent task set's dev split unless --split test is given:
//   records                  the records each person can see, every reference query's rows and statements the guard refuses
//   sql                      Text-to-SQL with llama3.2 against the reference queries, under four set-ups
//   run --config <name>      answer every task with a configuration and record the outcomes (--ids A01,A12 for some)
//   compare [--configs a,b]  every configuration scored from its recorded outcomes, against the first one
//   trace --id <task> [--config agent]   one task's trajectory, step by step
//   guard                    the allow-list, a write that waits for approval and a step budget
//   mcp                      the MCP server (start Ch17.Knowledge.Mcp first) as three people, without a token, and its cost
// Configurations: pipeline, workflow, agent, agent-af, agent-mcp (needs the MCP server and a token per asker in this
// tool's user secrets, Mcp:Tokens:<person id>). Model calls need Ollama with llama3.2; every call is recorded
// in runs/ch17 (answers seeded from Chapter 14's recordings), so a rerun replays.
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "compare";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var lab = await Lab.LoadAsync(Option("--split") ?? "dev");
var configurations = await Configurations.CreateAsync(lab);
var tasks = Option("--ids") is { } ids ? [.. lab.Tasks.Where(t => ids.Split(',').Contains(t.Id))] : lab.Tasks;

await (command switch
{
    "records" => Task.Run(() => Commands.Records(lab)),
    "sql" => Commands.SqlAsync(lab, configurations, tasks),
    "run" => Commands.RunAsync(lab, configurations, Option("--config") ?? "agent", tasks),
    "compare" => Task.Run(() => Commands.Compare(lab, (Option("--configs") ?? string.Join(',', Configurations.Names)).Split(','))),
    "trace" => Commands.TraceAsync(lab, configurations, Option("--id") ?? "A12", Option("--config") ?? "agent"),
    "guard" => Commands.GuardAsync(lab, configurations),
    "mcp" => Commands.McpAsync(lab),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
