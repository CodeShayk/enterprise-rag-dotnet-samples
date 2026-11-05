using System.Text;
using Ch16.Corrective.Cli;
using Contoso.Knowledge.Evaluation;

// Commands (from the sample-code folder), on golden set v2's dev split unless --split test is given:
//   graders [--llm]             evidence graders against "the evidence holds every labeled fact"
//   corrections                 what each correction does to the questions graded below correct
//   run --config <name>         answer every question with a configuration and record the outcomes
//   verify                      claim checks on the single pass's answers, and against Chapter 15's labels
//   compare [--configs a,b,…]   every configuration scored from its recorded outcomes, against the first one
//           [--diff]            also lists the questions each configuration answers differently from the one before
//   trace --id <question> [--config crag]   one question through the loop, step by step
//   workflow [--sample n]       the loop as an Agent Framework workflow against the state machine
//   outage [--sample n]         the verify configuration with Ollama stopped: the circuit breaker and degraded answers
//   verify --detail             also lists each re-citation with its scores and label
// Configurations: single, verify, verify-regenerate, crag, adaptive. Generation needs Ollama with llama3.2; every call is
// recorded in runs/ch16, seeded from Chapter 14's recordings, so a single pass that hasn't changed replays.
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "compare";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var lab = await Lab.LoadAsync(Option("--split") ?? "dev");
var pipelines = await Pipelines.CreateAsync(lab);
var questions = Option("--ids") is { } ids ? [.. lab.Questions.Where(q => ids.Split(',').Contains(q.Id))]
    : Option("--sample") is { } n ? lab.Questions.Take(int.Parse(n)).ToList() : lab.Questions;

await (command switch
{
    "graders" => Commands.GradersAsync(lab, pipelines, args.Contains("--llm")),
    "corrections" => Commands.CorrectionsAsync(lab, pipelines),
    "run" => Commands.RunAsync(lab, pipelines, Option("--config") ?? "single", questions),
    "verify" => Task.Run(() => Commands.Verify(lab, pipelines, args.Contains("--detail"))),
    "compare" => Task.Run(() => Commands.Compare(lab, (Option("--configs") ?? string.Join(',', Pipelines.Names)).Split(','), new CitationScorer(new ClaimSupport(Lab.Nli()), 0.05), args.Contains("--diff"))),
    "trace" => Commands.TraceAsync(lab, pipelines, Option("--id") ?? "G2-H042", Option("--config") ?? "crag"),
    "workflow" => Commands.WorkflowAsync(lab, pipelines, questions),
    "outage" => Commands.OutageAsync(pipelines, questions),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
