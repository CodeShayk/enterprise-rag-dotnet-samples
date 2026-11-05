using System.Text;
using System.Text.Json;
using Ch09.Chunking.Cli;
using Contoso.AI;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OllamaSharp;

// Commands (from the sample-code folder):
//   golden [--write]     build golden set v2 from v1, the tables and the written questions; --write saves it
//   golden-check         locate every span of golden set v2 and count the questions by split, type and collection
//   chunks <doc> [--strategy <name>]       print one document's chunks
//   stats [--semantic]                      chunks, sizes, time and evidence spans cut by a boundary, per strategy
//   experiment [--semantic] [--split dev]   Recall@4, nDCG@4, sufficiency and evidence tokens per strategy and collection
//   evidence <question id> [--strategy <name>] [--top 1]   what a strategy hands the model for one question
//   enrich [--collection manuals]           llama3.2 writes context for each structure-300 chunk of a collection (Ollama, CPU)
// --semantic adds the two semantic chunkers, which embed with qwen3-embedding:0.6b in a local Ollama (CPU only).
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "golden-check";
var corpus = await Corpus.LoadAsync();
var v2Path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
// Ollama on Windows opens several short-lived connections to its runner per input. A long embedding run piles up
// sockets in TIME_WAIT until a request fails with "actively refused", so wait and retry, and count the retries.
var retries = 0;
IEmbeddingGenerator<string, Embedding<float>>? embedder = args.Contains("--semantic")
    ? AIClientFactory.CreateEmbeddingGenerator(new AIOptions { OllamaEndpoint = "http://127.0.0.1:11434", Embeddings = new EmbeddingOptions { Provider = EmbeddingProvider.Ollama, Model = Experiment.EmbeddingModel } })
        .AsBuilder()
        .Use(async (values, options, inner, cancellationToken) =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await inner.GenerateAsync(values, options, cancellationToken);
                }
                catch (OllamaSharp.Models.Exceptions.OllamaException) when (attempt < 6)
                {
                    retries++;
                    await Task.Delay(TimeSpan.FromSeconds(2 << attempt), cancellationToken);
                }
            }
        })
        .Build()
    : null;

switch (command)
{
    case "golden":
    {
        var questions = GoldenSetV2.Build(corpus, GoldenSet.Load(), out var problems);
        foreach (var p in problems) Console.WriteLine($"problem: {p}");
        Console.WriteLine($"{questions.Count} questions, {questions.Count(q => q.Answerable)} answerable");
        if (args.Contains("--write"))
        {
            var target = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Shared", "Contoso.Corpus", "golden-set.v2.jsonl"));
            var options = new JsonSerializerOptions(JsonSerializerOptions.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            await File.WriteAllLinesAsync(target, questions.Select(q => JsonSerializer.Serialize(q, options)), new UTF8Encoding(false));
            Console.WriteLine($"wrote {target}");
        }

        break;
    }

    case "golden-check":
    {
        var questions = GoldenSet.Load(v2Path);
        var located = questions.Where(q => q.Answerable).Select(q => (Question: q, Spans: GoldenSet.Locate(q, corpus.Documents))).ToList();
        Console.WriteLine($"Golden set v2: {questions.Count} questions, {located.Count} answerable, {located.Sum(l => l.Spans.Count)} spans located, SHA-256 {GoldenSet.Sha256(v2Path)[..12]}");
        Console.WriteLine($"  by source: {Counts(questions.Select(q => q.Source))}");
        Console.WriteLine($"  by split:  {Counts(questions.Select(q => q.Split))}");
        Console.WriteLine($"  by type:   {Counts(questions.Select(q => q.Type))}");
        Console.WriteLine($"  answerable dev questions by collection: {Counts(located.Where(l => l.Question.Split == "dev").Select(l => corpus.Types[l.Spans[0].Doc]))}");
        break;
    }

    case "chunks":
    {
        var source = corpus.Sources.Single(s => s.Id == args[1]);
        var chunker = Experiment.Strategies(tokenizer, embedder).Single(c => c.Name == (Option("--strategy") ?? "structure-300"));
        foreach (var chunk in await chunker.SplitAsync(source))
        {
            Console.WriteLine($"--- {chunk.Kind} [{chunk.Start}..{chunk.End}) returns [{chunk.ReturnStart}..{chunk.ReturnEnd}), {tokenizer.CountTokens(chunk.IndexText)} tokens indexed");
            Console.WriteLine(chunk.IndexText);
        }

        break;
    }

    case "stats":
    {
        var questions = GoldenSet.Load(v2Path).Where(q => q.Answerable).ToList();
        var spans = questions.SelectMany(q => GoldenSet.Locate(q, corpus.Documents)).ToList();
        Console.WriteLine($"{"Strategy",-30} {"Chunks",6} {"Mean tok.",9} {"Max tok.",8} {"Time (ms)",9} {"Spans cut",10}");
        foreach (var chunker in Experiment.Strategies(tokenizer, embedder))
        {
            var chunked = await Experiment.ChunkAsync(chunker, corpus);
            var sizes = chunked.Chunks.Select(c => tokenizer.CountTokens(c.IndexText)).ToList();
            var (cut, total) = Experiment.CutSpans(chunked, spans);
            Console.WriteLine($"{chunked.Name,-30} {chunked.Chunks.Count,6} {sizes.Average(),9:F0} {sizes.Max(),8} {chunked.Elapsed.TotalMilliseconds,9:F0} {$"{cut} of {total}",10}" +
                chunker switch
                {
                    SemanticChunker semantic => $"   {semantic.Embedded} sentences embedded, {retries} retries",
                    SectionSemanticChunker sections => $"   {sections.Embedded} blocks of {sections.SectionsCut} long sections embedded",
                    HierarchicalChunker hierarchical => $"   {hierarchical.Summaries} summary nodes",
                    _ => "",
                });
        }

        break;
    }

    case "experiment":
    {
        var split = Option("--split") ?? "dev";
        var golden = GoldenSet.Load(v2Path);
        var questions = golden.Where(q => q.Split == split).ToList();
        var dev = golden.Where(q => q.Split == "dev").ToList();
        var harness = new Harness([.. corpus.Documents.Values], GoldenSet.Sha256(v2Path));
        var runs = new List<(string Name, RunRecord Run, double Tokens)>();
        var strategies = new List<(Chunked Chunked, RunRecord Dev)>();
        foreach (var chunker in Experiment.Strategies(tokenizer, embedder))
        {
            var chunked = await Experiment.ChunkAsync(chunker, corpus);
            var retrieval = new ChunkedRetrieval(chunked.Name, chunked.Chunks, corpus.Documents);
            var run = await harness.RunAsync(retrieval, questions, 4, split);
            runs.Add((chunked.Name, run, run.Answerable.Average(q => Experiment.EvidenceTokens(q.Retrieved, corpus, tokenizer))));
            strategies.Add((chunked, split == "dev" ? run : await harness.RunAsync(retrieval, dev, 4, "dev")));
        }

        // Route each collection's documents to the strategy chosen for it on the dev split, then score the mix as one index.
        var collectionOf = golden.Where(q => q.Answerable).ToDictionary(q => q.Id, q => corpus.Types[q.Relevant[0].Doc]);
        var choice = Experiment.Choose(strategies, "fixed-200", collectionOf, corpus, tokenizer);
        var best = strategies.MaxBy(s => s.Dev.Mean("Sufficient")).Chunked.Name;
        var routed = strategies.SelectMany(s => s.Chunked.Chunks.Where(c => choice.GetValueOrDefault(corpus.Types[c.Doc], best) == s.Chunked.Name)).ToList();
        var routedRun = await harness.RunAsync(new ChunkedRetrieval("per-collection", routed, corpus.Documents), questions, 4, split);
        runs.Add(("per-collection", routedRun, routedRun.Answerable.Average(q => Experiment.EvidenceTokens(q.Retrieved, corpus, tokenizer))));

        var baseline = runs.Single(r => r.Name == "fixed-200");
        Console.WriteLine($"Golden set v2, {split}: {baseline.Run.Answerable.Count()} answerable questions; BM25, k = 4");
        Console.WriteLine();
        Console.WriteLine($"{"Strategy",-30} {"Recall@4",8} {"nDCG@4",7} {"Suff.",6} {"Tokens",7}   {"Sufficient against fixed-200",-30}");
        foreach (var (name, run, tokens) in runs)
        {
            var d = Bootstrap.Paired([.. baseline.Run.Answerable.Select(q => q.Sufficient)], [.. run.Answerable.Select(q => q.Sufficient)]);
            var vs = name == "fixed-200" ? "" : $"{d.Mean:+0.00;-0.00} [{d.Low:+0.00;-0.00}, {d.High:+0.00;-0.00}]";
            Console.WriteLine($"{name,-30} {run.Mean("Recall"),8:F2} {run.Mean("nDCG"),7:F2} {run.Mean("Sufficient"),6:F2} {tokens,7:F0}   {vs}");
        }

        // Sufficiency per collection: which strategy to use where.
        var collections = baseline.Run.Answerable.Select(q => Collection(q.Id)).Distinct().Order().ToList();
        Console.WriteLine();
        Console.WriteLine($"{"Sufficient by collection",-30} " + string.Join(" ", collections.Select(c => $"{Short(c),10}")));
        foreach (var (name, run, _) in runs)
        {
            Console.WriteLine($"{name,-30} " + string.Join(" ", collections.Select(c => $"{run.Answerable.Where(q => Collection(q.Id) == c).Average(q => q.Sufficient),10:F2}")));
        }

        Console.WriteLine($"{"questions",-30} " + string.Join(" ", collections.Select(c => $"{baseline.Run.Answerable.Count(q => Collection(q.Id) == c),10}")));

        Console.WriteLine();
        Console.WriteLine("Chosen on dev: " + string.Join("; ", choice.OrderBy(c => c.Key).Select(c => $"{c.Key} → {c.Value}")));
        var single = runs.Single(r => r.Name == best);
        var routedVsBest = Bootstrap.Paired([.. single.Run.Answerable.Select(q => q.Sufficient)], [.. routedRun.Answerable.Select(q => q.Sufficient)]);
        Console.WriteLine($"per-collection against {best} (best single strategy on dev): {routedVsBest.Mean:+0.00;-0.00} [{routedVsBest.Low:+0.00;-0.00}, {routedVsBest.High:+0.00;-0.00}]");
        break;

        string Collection(string id) => corpus.Types[questions.Single(q => q.Id == id).Relevant[0].Doc];
        static string Short(string c) => c.Split(' ')[0];
    }

    case "evidence":
    {
        // What one strategy hands the model for one question: the top results, as the prompt would quote them.
        var question = GoldenSet.Load(v2Path).Single(q => q.Id == args[1]);
        var chunker = Experiment.Strategies(tokenizer, embedder).Single(c => c.Name == (Option("--strategy") ?? "structure-300"));
        var chunked = await Experiment.ChunkAsync(chunker, corpus);
        var retrieval = new ChunkedRetrieval(chunked.Name, chunked.Chunks, corpus.Documents);
        var top = int.Parse(Option("--top") ?? "1");
        var asker = Contoso.Corpus.People.Find(question.Asker)!;
        Console.WriteLine($"{question.Id}: {question.Question} ({chunked.Name})");
        foreach (var span in await retrieval.RetrieveAsync(question.Question, asker, top))
        {
            var chunk = chunked.Chunks.First(c => c.Doc == span.Doc && c.ReturnStart == span.Start && c.ReturnEnd == span.End);
            Console.WriteLine($"[{span.Doc}, {tokenizer.CountTokens(corpus.Documents[span.Doc].Text.AsSpan(span.Start, span.End - span.Start))} tokens]");
            if (chunk.Header is not null) Console.WriteLine(chunk.Header);
            Console.WriteLine(corpus.Documents[span.Doc].Text[span.Start..span.End]);
        }

        break;
    }

    case "enrich":
    {
        // Contextual retrieval on a budget: one collection, one call per chunk, results saved for the experiment.
        var collection = Option("--collection") ?? "manuals";
        var chat = new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = "llama3.2", OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 8192))
            .Build();
        var chunker = new StructureChunker(tokenizer, 300);
        Directory.CreateDirectory(Path.GetDirectoryName(Experiment.ContextPath)!);
        // Appends, so each collection's context adds to the others; the experiment keeps the newest line per chunk.
        await using var output = new StreamWriter(Experiment.ContextPath, append: true, new UTF8Encoding(false));
        var total = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        foreach (var source in corpus.Sources.Where(s => corpus.Types[s.Id] == collection))
        {
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var enriched = await Enrichment.WithModelContextAsync(chunk, source, chat, documentTokens: 6000);
                var context = enriched.Header![(chunk.Header?.Length ?? -1)..].Trim();
                var line = new Experiment.ContextLine(chunk.Doc, chunk.Start, chunk.End, context, Math.Round(stopwatch.Elapsed.TotalSeconds, 1));
                await output.WriteLineAsync(JsonSerializer.Serialize(line, JsonSerializerOptions.Web));
                if (count++ < 3) Console.WriteLine($"{chunk.Doc} [{chunk.Start}..{chunk.End}) {line.Seconds:F1} s: {context}");
            }
        }

        Console.WriteLine($"{count} chunks in {total.Elapsed.TotalMinutes:F1} minutes, {total.Elapsed.TotalSeconds / count:F1} s per chunk");
        break;
    }

    default:
        Console.WriteLine("Commands: golden [--write], golden-check, chunks, stats, experiment, enrich");
        break;
}

static string Counts(IEnumerable<string> values) =>
    string.Join(", ", values.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
