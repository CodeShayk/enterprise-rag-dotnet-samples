using System.Text;
using Ch15.Evaluation.Cli;

// Commands (from the sample-code folder). They evaluate the answers Chapter 14 recorded in runs/ch14/answers:
//   worksheet [--from n --to m]   the labeling worksheet for the 60-answer dev sample
//   checks                    deterministic checks over every recorded answer; no model
//   nli                       NLI support against the claim labels, both models, with and without windows
//   explain --id <question> --claim <n>   one shipped claim against its block and each window
//   citations [--split test]  citation recall and precision, wrong citations and fabricated sources, Chapter 3 against shipped
//   judge                     llama3.2 as a judge: agreement with the answer labels, position and verbosity bias
//   meai                      Microsoft.Extensions.AI.Evaluation with response caching and an HTML report
//   regress [--write-baseline] [--candidates a,b]   groundedness by question type against the regression baseline
//   errors                    where each labeled failure was lost, and the failure cases for review
// judge and meai need Ollama with llama3.2; the NLI commands need `python eng/get-models.py`.
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "checks";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var threshold = Option("--threshold") is { } t ? double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : Lab.SupportThreshold;
var baseline = Path.GetFullPath(Path.Combine(Lab.Runs, "..", "Chapter15-Answer-Evaluation", "Ch15.Regression.Tests", "baseline", "groundedness.dev.json"));
var lab = await Lab.LoadAsync();

await (command switch
{
    "worksheet" => Task.Run(() => Commands.Worksheet(lab, int.Parse(Option("--from") ?? "0"), int.Parse(Option("--to") ?? "60"))),
    "checks" => Task.Run(() => Commands.Checks(lab)),
    "nli" => Task.Run(() => Commands.Nli(lab)),
    "explain" => Task.Run(() => Commands.Explain(Option("--id") ?? "G2-H025", int.Parse(Option("--claim") ?? "1"))),
    "citations" => Task.Run(() => Commands.Citations(lab, threshold, Option("--split") ?? "dev")),
    "judge" => ModelCommands.JudgeAsync(lab),
    "meai" => ModelCommands.MeaiAsync(lab, int.Parse(Option("--count") ?? "60"), threshold),
    "regress" => Task.Run(() => Commands.Regress(lab, baseline, (Option("--candidates") ?? "compressed,grounded,best-last,raw-tables,chapter3").Split(','),
        args.Contains("--write-baseline"), double.Parse(Option("--tolerance") ?? "0.05", System.Globalization.CultureInfo.InvariantCulture), threshold)),
    "errors" => Task.Run(() => Commands.Errors(lab, threshold)),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
