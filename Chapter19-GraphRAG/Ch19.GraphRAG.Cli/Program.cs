using System.Text;
using Ch19.GraphRAG.Cli;

// Commands (from the sample-code folder):
//   build [--model]          the graph from every document: rules and table rows, and llama3.2 for prose with --model
//   extraction [--no-model]  extraction precision and recall against 69 labeled edges in 15 documents
//   resolve                  NER proposals, entity resolution with its audit log, a split, and the identifier trap
//   store                    the graph in memory, DuckDB and PostgreSQL (Docker): load, two hops, a cascading delete
//   paths [--id M19]         one question through the graph for each asker: linked entities, paths, PageRank, evidence
//   multihop [--split test] [--answers]   evidence (and answers) for hybrid, graph and hybrid+graph retrieval
//   global                   Louvain communities, summaries made on demand and map-reduce answers to global questions
//   fresh                    an incremental update from SOP-FS-014 version 2 to 3 against a full rebuild
// Model calls are recorded in runs/ch19 and replay; new ones need Ollama with llama3.2.
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "build";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var lab = await Lab.LoadAsync();

await (command switch
{
    "build" => Extract.BuildAsync(lab, args.Contains("--model")),
    "extraction" => Extract.EvaluateAsync(lab, !args.Contains("--no-model")),
    "resolve" => Resolve.RunAsync(lab),
    "store" => Retrieve.StoreAsync(lab),
    "paths" => Retrieve.PathsAsync(lab, Option("--id") ?? "M19"),
    "multihop" => Retrieve.MultiHopAsync(lab, Option("--split") ?? "dev", args.Contains("--answers")),
    "global" => Global.RunAsync(lab),
    "fresh" => Global.FreshAsync(lab),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
