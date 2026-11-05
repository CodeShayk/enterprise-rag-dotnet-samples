using System.Text;
using Ch14.Generation.Cli;

// Commands (from the sample-code folder), on golden set v2's dev split unless --split test is given:
//   context                  what each assembly step does to the evidence's size and facts; no model
//   answer --setup <name> [--sample n | --ids a,b]   answer the questions with one setup and record the answers
//   compare --setups a,b,... [--sample n]  score recorded answers and compare each setup with the first
//   latency --setups a,b,...  prompt tokens against time to first token over the recorded answers
//   show --setup <name> --id <question>   one recorded answer with its citations and their validation
//   quotes --setup <name>    what validation found in the recorded claims, and why quotes weren't found
//   turns                    follow-ups answered with and without the conversation, and "make that shorter"
//   stream --id <question>   one answer streamed live, with the time of every event
// Setups: chapter3, grounded, best-last, compressed, raw-tables, drop-below-0.1.
// Generation needs Ollama with llama3.2; every call is recorded in runs/ch14 and replayed on reruns.
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "context";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var lab = await Lab.LoadAsync(Option("--split") ?? "dev");
var questions = Option("--ids") is { } ids ? [.. lab.Questions.Where(q => ids.Split(',').Contains(q.Id))]
    : Option("--sample") is { } n ? Commands.Sample(lab.Questions, int.Parse(n)) : lab.Questions;

await (command switch
{
    "context" => Commands.ContextAsync(lab),
    "answer" => Commands.AnswerAsync(lab, Option("--setup") ?? "grounded", questions),
    "compare" => Task.Run(() => Commands.Compare(lab, (Option("--setups") ?? "chapter3,grounded").Split(','), questions)),
    "latency" => Task.Run(() => Commands.Latency(lab, (Option("--setups") ?? "chapter3,grounded").Split(','))),
    "show" => Task.Run(() => Commands.Show(lab, Option("--setup") ?? "grounded", Option("--id") ?? "G001")),
    "quotes" => Task.Run(() => Commands.Quotes(lab, Option("--setup") ?? "grounded")),
    "turns" => Commands.TurnsAsync(lab),
    "stream" => Commands.StreamAsync(lab, Option("--id") ?? "G003"),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
