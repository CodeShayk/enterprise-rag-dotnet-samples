using System.Text;
using Ch10.Embeddings.Cli;
using Contoso.AI;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

// Commands (from the sample-code folder):
//   inspect                          the model's inputs and outputs, how it splits Contoso's identifiers, chunk lengths in its tokens
//   retrieval [--split dev] [--ollama]   dense retrieval on golden set v2 against Chapter 9's BM25 (--ollama adds qwen3-embedding:0.6b)
//   breadcrumbs [--split dev]        structure-aware chunks with and without breadcrumbs, under BM25 and dense retrieval
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "inspect";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
// A difference that rounds to zero prints as +0.00, not -0.00 or -+0.00.
static string Signed(double x) => (Math.Round(x, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("+0.00;-0.00");
var o200k = TiktokenTokenizer.CreateForEncoding("o200k_base");

switch (command)
{
    case "inspect":
    {
        using var model = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true));
        Console.WriteLine($"{model.Spec.Name}: inputs {string.Join(", ", model.InputNames)}; output {model.OutputNames[0]}; " +
            $"{model.Spec.Dimensions} dimensions, {model.Spec.Pooling} pooling, {model.Spec.MaxTokens} tokens");
        var wordPiece = BertTokenizer.Create(model.Spec.VocabPath, new BertOptions { LowerCaseBeforeTokenization = true, RemoveNonSpacingMarks = true });
        foreach (var text in new[] { "PN-4471-C", "E-217", "SOP-FS-014", "85 N·m", "superseded" })
        {
            var pieces = wordPiece.EncodeToTokens(text, out _).Select(t => t.Value);
            Console.WriteLine($"  {text,-12} {string.Join(" ", pieces)}");
        }

        // How long the chunks Chapter 9 chose are in the embedding model's own tokens, against the o200k budget.
        var corpus = await Corpus.LoadAsync();
        foreach (var chunker in new IChunker[] { new ParentChildChunker(o200k, 100, 600), new StructureChunker(o200k, 300) })
        {
            var chunks = new List<Chunk>();
            foreach (var source in corpus.Sources) chunks.AddRange(await chunker.SplitAsync(source));
            var cutBefore = model.Truncated;
            var lengths = chunks.Select(c => (WordPiece: model.Tokenize(c.IndexText).Count, O200k: o200k.CountTokens(c.IndexText))).ToList();
            Console.WriteLine($"{chunker.Name}: {chunks.Count} chunks; WordPiece/o200k {lengths.Sum(l => l.WordPiece) / (double)lengths.Sum(l => l.O200k):F2}; " +
                $"longest {lengths.Max(l => l.WordPiece)} WordPiece tokens; cut at {model.Spec.MaxTokens}: {model.Truncated - cutBefore}");
        }

        break;
    }

    case "retrieval":
    {
        var split = Option("--split") ?? "dev";
        var corpus = await Corpus.LoadAsync();
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        var questions = GoldenSet.Load(path).Where(q => q.Split == split).ToList();
        var harness = new Harness([.. corpus.Documents.Values], GoldenSet.Sha256(path));

        var parentChild = await Chunks(new ParentChildChunker(o200k, 100, 600));
        var structure = await Chunks(new EnrichedChunker(new StructureChunker(o200k, 300), "entities", Enrichment.WithEntities));
        using var fp32 = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: false));
        using var int8 = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true));
        using var bare = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true, queryInstruction: false));
        var hashing = new HashingEmbeddingGenerator();

        var configurations = new List<IRetrievalUnderTest>
        {
            new Bm25Retrieval("BM25 · parent-child", parentChild, corpus.Documents),
            await DenseRetrieval.CreateAsync("hashing · parent-child", parentChild, hashing, hashing, corpus.Documents),
            await Dense("bge-small fp32 · parent-child", parentChild, fp32),
            await Dense("bge-small int8 · parent-child", parentChild, int8),
            await Dense("int8, no query instruction", parentChild, bare),
            await Dense("bge-small int8 · structure+entities", structure, int8),
        };
        if (args.Contains("--ollama"))
        {
            configurations.Add(await DenseRetrieval.CreateAsync("qwen3-embedding:0.6b · parent-child", parentChild,
                Ollama.Embedder(""), Ollama.Embedder(Ollama.QueryInstruction), corpus.Documents));
        }

        var runs = new List<RunRecord>();
        foreach (var configuration in configurations) runs.Add(await harness.RunAsync(configuration, questions, 4, split));

        var baseline = runs[0];
        Console.WriteLine($"Golden set v2, {split}: {baseline.Answerable.Count()} answerable questions; k = 4");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-38} {"Recall@4",8} {"nDCG@4",7} {"Suff.",6}   Sufficient against BM25");
        foreach (var run in runs)
        {
            var d = Bootstrap.Paired([.. baseline.Answerable.Select(q => q.Sufficient)], [.. run.Answerable.Select(q => q.Sufficient)]);
            Console.WriteLine($"{run.Configuration,-38} {run.Mean("Recall"),8:F2} {run.Mean("nDCG"),7:F2} {run.Mean("Sufficient"),6:F2}   " +
                (run == baseline ? "" : $"{Signed(d.Mean)} [{Signed(d.Low)}, {Signed(d.High)}]"));
        }

        // Where lexical and dense retrieval part ways: sufficiency by question type.
        var types = questions.Where(q => q.Answerable).GroupBy(q => q.Type).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        Console.WriteLine();
        Console.WriteLine($"{"Sufficient by question type",-38} " + string.Join(" ", types.Select(t => $"{t,10}")));
        foreach (var run in runs)
        {
            Console.WriteLine($"{run.Configuration,-38} " + string.Join(" ", types.Select(t => $"{run.Answerable.Where(q => q.Type == t).Average(q => q.Sufficient),10:F2}")));
        }

        Console.WriteLine($"{"questions",-38} " + string.Join(" ", types.Select(t => $"{questions.Count(q => q.Answerable && q.Type == t),10}")));
        break;

        async Task<IReadOnlyList<Chunk>> Chunks(IChunker chunker)
        {
            var chunks = new List<Chunk>();
            foreach (var source in corpus.Sources) chunks.AddRange(await chunker.SplitAsync(source));
            return chunks;
        }

        Task<DenseRetrieval> Dense(string name, IReadOnlyList<Chunk> chunks, OnnxEmbeddingModel model) =>
            DenseRetrieval.CreateAsync(name, chunks, new OnnxEmbeddingGenerator(model), new OnnxEmbeddingGenerator(model, queries: true), corpus.Documents);
    }

    case "breadcrumbs":
    {
        // Chapter 9 measured breadcrumbs with BM25; the same chunks with and without them, under dense retrieval too.
        var split = Option("--split") ?? "dev";
        var corpus = await Corpus.LoadAsync();
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        var questions = GoldenSet.Load(path).Where(q => q.Split == split).ToList();
        var harness = new Harness([.. corpus.Documents.Values], GoldenSet.Sha256(path));
        using var int8 = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true));

        Console.WriteLine($"Golden set v2, {split}: {questions.Count(q => q.Answerable)} answerable questions; k = 4");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-38} {"Recall@4",8} {"nDCG@4",7} {"Suff.",6}");
        foreach (var dense in new[] { false, true })
        {
            var runs = new List<RunRecord>();
            foreach (var chunker in new[] { new StructureChunker(o200k, 300, breadcrumbs: false), new StructureChunker(o200k, 300) })
            {
                var chunks = new List<Chunk>();
                foreach (var source in corpus.Sources) chunks.AddRange(await chunker.SplitAsync(source));
                IRetrievalUnderTest configuration = dense
                    ? await DenseRetrieval.CreateAsync($"bge-small int8 · {chunker.Name}", chunks,
                        new OnnxEmbeddingGenerator(int8), new OnnxEmbeddingGenerator(int8, queries: true), corpus.Documents)
                    : new Bm25Retrieval($"BM25 · {chunker.Name}", chunks, corpus.Documents);
                var run = await harness.RunAsync(configuration, questions, 4, split);
                runs.Add(run);
                Console.WriteLine($"{run.Configuration,-38} {run.Mean("Recall"),8:F2} {run.Mean("nDCG"),7:F2} {run.Mean("Sufficient"),6:F2}");
            }

            var (bare, crumbs) = (runs[0].Answerable.ToList(), runs[1].Answerable.ToList());
            var sufficient = Bootstrap.Paired([.. bare.Select(q => q.Sufficient)], [.. crumbs.Select(q => q.Sufficient)]);
            var ndcg = Bootstrap.Paired([.. bare.Select(q => q.Ndcg)], [.. crumbs.Select(q => q.Ndcg)]);
            Console.WriteLine($"  breadcrumbs against bare: sufficient {Signed(sufficient.Mean)} [{Signed(sufficient.Low)}, {Signed(sufficient.High)}], " +
                $"nDCG@4 {Signed(ndcg.Mean)} [{Signed(ndcg.Low)}, {Signed(ndcg.High)}]");
        }

        break;
    }

    case "throughput":
    {
        // Real chunks, repeated to 960: the cost of embedding Chapter 9's structure-aware chunks on this CPU.
        var corpus = await Corpus.LoadAsync();
        var chunks = new List<Chunk>();
        foreach (var source in corpus.Sources) chunks.AddRange(await new StructureChunker(o200k, 300).SplitAsync(source));
        var texts = Enumerable.Range(0, 960).Select(i => chunks[i % chunks.Count].IndexText).ToList();
        var sorted = texts.OrderBy(t => t.Length).ToList();
        Console.WriteLine($"{chunks.Count} chunks, {texts.Average(t => o200k.CountTokens(t)):F0} tokens on average; {Environment.ProcessorCount} logical processors; 10 seconds per row");
        Console.WriteLine();
        Console.WriteLine($"{"Weights",-8} {"Batch",-14} {"Threads per run",15} {"Concurrent runs",15} {"Chunks/s",9} {"Per thread",10}");
        foreach (var int8 in new[] { false, true })
        {
            foreach (var (batch, bySize, threads, callers) in new[] { (1, false, 1, 1), (32, false, 1, 1), (32, true, 1, 1), (32, true, 14, 1), (32, true, 1, 14) })
            {
                using var model = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8), threads);
                // Sorting by length before batching means each batch pads its chunks to a similar length.
                var batches = (bySize ? sorted : texts).Chunk(batch).ToList();
                var done = 0;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Parallel.For(0, callers, caller =>
                {
                    for (var i = caller; stopwatch.Elapsed < TimeSpan.FromSeconds(10); i = (i + callers) % batches.Count)
                    {
                        model.Embed(batches[i]);
                        Interlocked.Add(ref done, batches[i].Length);
                    }
                });
                var rate = done / stopwatch.Elapsed.TotalSeconds;
                Console.WriteLine($"{(int8 ? "int8" : "fp32"),-8} {(batch == 1 ? "1" : bySize ? "32, by length" : "32"),-14} {threads,15} {callers,15} {rate,9:F0} {rate / (threads * callers),10:F1}");
            }
        }

        break;
    }

    case "cache":
    {
        // Embeddings cached by content: a second run over unchanged chunks never reaches the model.
        var corpus = await Corpus.LoadAsync();
        var chunks = new List<Chunk>();
        foreach (var source in corpus.Sources) chunks.AddRange(await new StructureChunker(o200k, 300).SplitAsync(source));
        using var model = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true));
        var cache = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));
        var embedded = 0;
        var embedder = new OnnxEmbeddingGenerator(model).AsBuilder()
            .UseDistributedCache(cache, c => c.CacheKeyAdditionalValues = [model.Spec.Name])
            .Use(async (values, options, inner, cancellationToken) =>
            {
                var list = values.ToList();
                Interlocked.Add(ref embedded, list.Count);
                return await inner.GenerateAsync(list, options, cancellationToken);
            })
            .Build();
        var edited = chunks.Select((c, i) => i % 15 == 0 ? c.IndexText + " Revised 2027-01-15." : c.IndexText).ToList();
        foreach (var (name, texts) in new[] { ("first run", chunks.Select(c => c.IndexText).ToList()), ("second run, nothing changed", chunks.Select(c => c.IndexText).ToList()), ("third run, 11 chunks edited", edited) })
        {
            embedded = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await embedder.GenerateAsync(texts);
            Console.WriteLine($"{name,-30} {texts.Count} chunks, {embedded,3} embedded, {stopwatch.Elapsed.TotalSeconds,5:F2} s");
        }

        break;
    }

    case "synth":
    {
        // The 100,000-entry log that the compression, index and filter experiments share, embedded once.
        var entries = Synthetic.Entries(100_000);
        var queries = Synthetic.Queries(500);
        using var model = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), intraOpThreads: 1);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var vectors = Synthetic.Embed(model, [.. entries.Select(e => e.Text)]);
        var seconds = stopwatch.Elapsed.TotalSeconds;
        Synthetic.Save("logs.bin", vectors);
        Synthetic.Save("queries.bin", Synthetic.Embed(model, queries, model.Spec.QueryPrefix));
        Synthetic.SaveEntries(entries);
        Console.WriteLine($"{entries.Count:N0} entries, {entries.Average(e => o200k.CountTokens(e.Text)):F0} tokens on average, embedded in {seconds:F0} s " +
            $"({entries.Count / seconds:F0} a second); {queries.Count} queries; written to {Synthetic.Folder}");
        foreach (var entry in entries.Take(3)) Console.WriteLine($"  {entry.Text}");
        Console.WriteLine($"  {queries[0]}");
        break;
    }

    case "compress":
    {
        // Recall@10 of each compressed form against exact search over full-precision vectors, 500 queries.
        var vectors = Synthetic.Load("logs.bin");
        var queries = Synthetic.Load("queries.bin");
        var exact = new ExactIndex(vectors);
        var truth = queries.AsParallel().AsOrdered().Select(q => exact.Search(q, 10).Select(r => r.Index).ToHashSet()).ToArray();
        var int8 = vectors.Select(v => VectorCompression.ToInt8(v)).ToArray();
        var bits = vectors.Select(v => VectorCompression.ToBits(v)).ToArray();
        Console.WriteLine($"{vectors.Length:N0} vectors of {vectors[0].Length} dimensions, {queries.Length} queries; recall@10 against exact float32 search");
        Console.WriteLine();
        Console.WriteLine($"{"Stored as",-36} {"Bytes per vector",16} {"Recall@10",9}");
        Row("float32 (exact search)", 4 * 384, i => exact.Search(queries[i], 10).Select(r => r.Index));
        Row("int8, one scale per vector", 384 + 4, i => { var q = VectorCompression.ToInt8(queries[i]); return Top(j => VectorCompression.DotInt8(q, int8[j]), 10); });
        var quantizer = new VectorCompression.ScalarQuantizer(vectors);
        var bytes = vectors.Select(v => quantizer.Encode(v)).ToArray();
        Row("uint8, a range per dimension", 384, i => Top(j => quantizer.Score(queries[i], bytes[j]), 10));
        Row("uint8, top 40 rescored in float32", 384, i => Top(j => quantizer.Score(queries[i], bytes[j]), 40)
            .OrderByDescending(j => System.Numerics.Tensors.TensorPrimitives.Dot(queries[i], vectors[j])).Take(10));
        Row("binary, one bit per dimension", 384 / 8, i => { var q = VectorCompression.ToBits(queries[i]); return Top(j => -VectorCompression.Hamming(q, bits[j]), 10); });
        foreach (var candidates in new[] { 40, 100 })
        {
            Row($"binary, top {candidates} rescored in float32", 384 / 8, i =>
            {
                var q = VectorCompression.ToBits(queries[i]);
                return Top(j => -VectorCompression.Hamming(q, bits[j]), candidates)
                    .OrderByDescending(j => System.Numerics.Tensors.TensorPrimitives.Dot(queries[i], vectors[j])).Take(10);
            });
        }

        foreach (var dimensions in new[] { 256, 128 })
        {
            var truncated = new ExactIndex([.. vectors.Select(v => VectorCompression.Truncate(v, dimensions))]);
            Row($"first {dimensions} dimensions, re-normalized", 4 * dimensions, i => truncated.Search(VectorCompression.Truncate(queries[i], dimensions), 10).Select(r => r.Index));
        }

        break;

        void Row(string name, int bytes, Func<int, IEnumerable<int>> search)
        {
            var recall = Enumerable.Range(0, queries.Length).AsParallel().Average(i => search(i).Count(truth[i].Contains) / 10.0);
            Console.WriteLine($"{name,-36} {bytes,16} {recall,9:F3}");
        }

        IEnumerable<int> Top(Func<int, float> score, int k)
        {
            var best = new PriorityQueue<int, float>(k + 1);
            for (var j = 0; j < vectors.Length; j++)
            {
                var s = score(j);
                if (best.Count < k) best.Enqueue(j, s);
                else if (best.TryPeek(out _, out var worst) && s > worst) best.EnqueueDequeue(j, s);
            }

            return best.UnorderedItems.OrderByDescending(x => x.Priority).Select(x => x.Element).ToList();
        }
    }

    case "indexes":
        await Indexes.RunAsync();
        break;

    case "stores":
    {
        var corpus = await Corpus.LoadAsync();
        var chunks = new List<Chunk>();
        foreach (var source in corpus.Sources) chunks.AddRange(await new ParentChildChunker(o200k, 100, 600).SplitAsync(source));
        await Stores.RunAsync(corpus, chunks);
        break;
    }

    case "reembed":
    {
        var corpus = await Corpus.LoadAsync();
        var chunks = new List<Chunk>();
        foreach (var source in corpus.Sources) chunks.AddRange(await new ParentChildChunker(o200k, 100, 600).SplitAsync(source));
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        await Lifecycle.RunAsync(corpus, chunks, [.. GoldenSet.Load(path).Where(q => q.Split == "dev" && q.Answerable)]);
        break;
    }

    default:
        Console.WriteLine("Commands: inspect, retrieval, throughput, cache, synth, compress, indexes");
        return 1;
}

return 0;

/// <summary>Chapter 9's chosen embedding model through a local Ollama, for comparison: 32 inputs per request and retries (Bible §8).</summary>
internal static class Ollama
{
    public const string QueryInstruction = "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:";

    public static IEmbeddingGenerator<string, Embedding<float>> Embedder(string prefix) =>
        AIClientFactory.CreateEmbeddingGenerator(new AIOptions { OllamaEndpoint = "http://127.0.0.1:11434", Embeddings = new EmbeddingOptions { Provider = EmbeddingProvider.Ollama, Model = "qwen3-embedding:0.6b" } })
            .AsBuilder()
            .Use(async (values, options, inner, cancellationToken) =>
            {
                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var batch in values.Chunk(32))
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            result.AddRange(await inner.GenerateAsync([.. batch.Select(v => prefix + v)], options, cancellationToken));
                            break;
                        }
                        catch (OllamaSharp.Models.Exceptions.OllamaException) when (attempt < 6)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2 << attempt), cancellationToken);
                        }
                    }
                }

                return result;
            })
            .Build();
}
