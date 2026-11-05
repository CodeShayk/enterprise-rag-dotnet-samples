using System.Text;
using Ch21.Performance.Cli;

// Commands (from the sample-code folder); answers replay from recordings unless a command says it runs live:
//   profile                         where an answer's time goes, every golden question (llama3.2 3B as recorded)
//   onnx                            the re-ranker under ONNX Runtime thread counts and batch sizes, and its cold start
//   prefix [--split test]           prompt layouts against Ollama's prompt cache (live, llama3.2 3B)
//   spot --model llama3.2 [--n 12]  answers live against their recordings, with CPU-seconds (live)
//   cascade [--split dev]           llama3.2:1b, llama3.2 3B and the cascade between them (1B live the first time)
//   load [--levels 1,2,4] [--seconds 240]   a closed-loop load test of the live pipeline
//   cache [--zipf 1.0]              what each cache layer saves, a simulated day, and a key that leaks
//   semantic                        semantic-cache thresholds on paraphrases and near misses
//   hybrid                          HybridCache over Redis in a container (needs Docker)
//   cost                            cost per answer and per tenant from the cascade run and the load test
//   lab                             the simulated day served by 3B or the cascade, with and without the answer cache
//   limits                          rate limits per tenant, a daily token quota and a gate that sheds load
// Live commands need Ollama on the CPU (Bible §8) with llama3.2 and llama3.2:1b pulled.
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "profile";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
if (command == "limits")
{
    await Operations.LimitsAsync();
    return;
}

var lab = await Lab.LoadAsync();
await (command switch
{
    "profile" => Profile.ProfileAsync(lab),
    "onnx" => Profile.OnnxAsync(lab),
    "prefix" => Models.PrefixAsync(lab, Option("--split") ?? "test"),
    "spot" => Models.SpotCheckAsync(lab, Option("--model") ?? Lab.Large, int.Parse(Option("--n") ?? "12")),
    "cascade" => Models.CascadeAsync(lab, Option("--split") ?? "dev"),
    "load" => Models.LoadAsync(lab, [.. (Option("--levels") ?? "1,2,4").Split(',').Select(int.Parse)], TimeSpan.FromSeconds(int.Parse(Option("--seconds") ?? "240"))),
    "cache" => Caches.LayersAsync(lab, zipf: double.Parse(Option("--zipf") ?? "1.0", System.Globalization.CultureInfo.InvariantCulture)),
    "semantic" => Caches.SemanticAsync(lab),
    "hybrid" => Caches.HybridAsync(lab),
    "cost" => Task.Run(() => Operations.Cost(lab, Option("--split") ?? "dev")),
    "lab" => Task.Run(() => Operations.Result(lab, Option("--split") ?? "dev")),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
