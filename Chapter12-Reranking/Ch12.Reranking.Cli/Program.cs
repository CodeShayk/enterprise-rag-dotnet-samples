using System.Text;
using Ch12.Reranking.Cli;

// Commands (from the sample-code folder), on golden set v2's dev split unless --split test is given:
//   rerank                  the hybrid against its candidates re-ranked by MiniLM-L6 (FP32, INT8) and MiniLM-L12
//   candidates [--model]    candidates re-ranked, from 4 to 40: quality and latency
//   truncate [--model]      what the cross-encoder reads: the parent at 128, 256 or 512 tokens, or the matched child
//   diversity [--model]     MMR and a per-document cap after re-ranking
//   calibrate [--model]     Platt scaling on dev, reliability on test, thresholds and "I don't know"
//   explain <id> [--model]  one question's candidates before and after re-ranking
//   llm [--count 20]        a pointwise LLM re-ranker through Ollama (llama3.2), against the cross-encoder
//   evaluate [--model] [--candidates 20] [--tokens 512] [--audited]   the re-ranker against the hybrid alone, and the questions it changed;
//                 --audited also accepts the paragraph that explains an error code where the label names its table row
// --model is L6 (the default), L6-int8 or L12.
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "evaluate";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var split = Option("--split") ?? "dev";
var model = Option("--model") ?? "L6";

await (command switch
{
    "rerank" => Commands.RerankAsync(await Lab.LoadAsync(split)),
    "candidates" => Commands.CandidatesAsync(await Lab.LoadAsync(split), model),
    "truncate" => Commands.TruncateAsync(await Lab.LoadAsync(split), model),
    "diversity" => Commands.DiversityAsync(await Lab.LoadAsync(split), model),
    "calibrate" => Commands.CalibrateAsync(model),
    "explain" => Commands.ExplainAsync(await Lab.LoadAsync(split), args[1], model),
    "bench" => Bench.RunAsync(await Lab.LoadAsync(split), model),
    "llm" => Llm.RunAsync(await Lab.LoadAsync(split), int.Parse(Option("--count") ?? "20"), model),
    "evaluate" => Commands.EvaluateAsync(await Lab.LoadAsync(split, args.Contains("--audited")), model, int.Parse(Option("--candidates") ?? "20"), int.Parse(Option("--tokens") ?? "512")),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
