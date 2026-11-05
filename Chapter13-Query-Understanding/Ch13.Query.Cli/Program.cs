using System.Text;
using Ch13.Query.Cli;

// Commands (from the sample-code folder), on golden set v2's dev split unless --split test is given:
//   normalize     one typo per question, searched as typed and after correction against the index's vocabulary
//   intent        intent checks: nearest written example, with an LLM below a margin, and the LLM alone
//   unsafe        requests that must be declined, against the intent check the planner keeps
//   condense      two-part questions asked as a question and a follow-up, condensed by llama3.2 or not
//   transform     rewriting, multi-query fusion, decomposition and HyDE, with their LLM cost
//   filters [--llm]   filters read from the question by rules, strict and expanded, or by llama3.2
//   route         the semantic router on held-out examples and the golden set
//   evaluate      the QueryPlanner end to end against Chapter 11's hybrid
// The LLM steps need Ollama with llama3.2; their answers are cached in runs/ch13/llm-cache.jsonl.
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "evaluate";
var split = args.SkipWhile(a => a != "--split").Skip(1).FirstOrDefault() ?? "dev";
var lab = await Lab.LoadAsync(split);

await (command switch
{
    "normalize" => Commands.NormalizeAsync(lab),
    "intent" => Commands.IntentAsync(lab),
    "unsafe" => Commands.UnsafeAsync(),
    "condense" => Commands.CondenseAsync(lab),
    "transform" => Commands.TransformAsync(lab),
    "filters" => Commands.FiltersAsync(lab, args.Contains("--llm")),
    "route" => Commands.RouteAsync(lab),
    "evaluate" => Commands.EvaluateAsync(lab),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
