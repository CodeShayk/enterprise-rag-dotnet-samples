using System.Diagnostics;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Retrieval;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.En;
using Lucene.Net.Analysis.Standard;

namespace Ch11.Hybrid.Cli;

public static class Commands
{
    private static readonly string GlossaryPath = Path.Combine(AppContext.BaseDirectory, "contoso-glossary.txt");

    /// <summary>The keyword retriever the chapter settles on: Lucene.NET with Contoso's analyzer. The glossary measured no gain.</summary>
    public static LuceneRetriever Keyword(Lab lab) => new("keyword: Lucene, Contoso analyzer", lab.Chunks, ContosoAnalyzers.Identifiers());

    public static async Task FailuresAsync(Lab lab)
    {
        using var model = Lab.Bge();
        var bm25 = lab.Bm25();
        var dense = await lab.DenseAsync(model);
        var (keyword, semantic) = (await lab.RunAsync(bm25), await lab.RunAsync(dense));
        Console.WriteLine($"Golden set v2, {lab.Split}: {keyword.Answerable.Count()} answerable questions; k = 4; BM25 as in Chapter 9, bge-small INT8 as in Chapter 10");
        Console.WriteLine();
        Console.WriteLine($"{"Questions",-22} {"Count",5} {"Both sufficient",16} {"BM25 only",10} {"Dense only",11} {"Neither",8}");
        var pairs = keyword.Answerable.Zip(semantic.Answerable)
            .Select(p => (Question: lab.Questions.First(q => q.Id == p.First.Id), Keyword: p.First.Sufficient, Dense: p.Second.Sufficient)).ToList();
        foreach (var (label, named) in new[] { ("naming an identifier", true), ("without one", false) })
        {
            var group = pairs.Where(p => Lab.HasIdentifier(p.Question.Question) == named).ToList();
            Console.WriteLine($"{label,-22} {group.Count,5} {group.Count(p => p.Keyword == 1 && p.Dense == 1),16} {group.Count(p => p.Keyword == 1 && p.Dense < 1),10} " +
                $"{group.Count(p => p.Keyword < 1 && p.Dense == 1),11} {group.Count(p => p.Keyword < 1 && p.Dense < 1),8}");
        }

        Console.WriteLine();
        Console.WriteLine("Where only one is sufficient: facts found in the top 4, and the rank of the first relevant parent in the top 20");
        foreach (var p in pairs.Where(p => p.Keyword != p.Dense).OrderByDescending(p => p.Keyword))
        {
            var (k, d) = (await lab.EvidenceAsync(bm25, p.Question), await lab.EvidenceAsync(dense, p.Question));
            Console.WriteLine($"  {p.Question.Id,-7} {Trim(p.Question.Question, 52),-52} BM25 {k.Found}/{k.Facts} ({Rank(k.First)})  dense {d.Found}/{d.Facts} ({Rank(d.First)})");
        }
    }

    /// <summary>One question's two rankings and their fusion, parent by parent: the data behind Figure 11.2.</summary>
    public static async Task ExplainAsync(Lab lab, string id)
    {
        using var model = Lab.Bge();
        using var keyword = Keyword(lab);
        var dense = await lab.DenseAsync(model);
        var question = lab.Questions.First(q => q.Id == id);
        var asker = Contoso.Corpus.People.Find(question.Asker)!;
        var located = GoldenSet.Locate(question, lab.Documents);
        string Label(Hit hit)
        {
            var (doc, start, end) = hit.Chunk.ReturnKey;
            var relevant = located.Any(s => s.Doc == doc && s.Start < end && start < s.End) ? "relevant" : "";
            var header = hit.Chunk.Chunk.Header?.Split(" › ").Last() ?? "";
            return $"{doc,-18} {Trim(header, 34),-34} {relevant}";
        }

        Console.WriteLine($"{question.Id} ({question.Asker}): {question.Question}");
        var ranks = new List<Dictionary<(string, int, int), int>>();
        foreach (var source in new IRetriever[] { keyword, dense })
        {
            var hits = (await source.RetrieveAsync(new RetrievalRequest(question.Question, asker, 20))).Hits;
            ranks.Add(hits.Select((h, i) => (h.Chunk.ReturnKey, i + 1)).ToDictionary(x => x.ReturnKey, x => x.Item2));
            Console.WriteLine($"{source.Name}:");
            for (var i = 0; i < Math.Min(6, hits.Count); i++) Console.WriteLine($"  {i + 1,2}  {hits[i].Score,7:0.000}  {Label(hits[i])}");
        }

        var fused = (await new HybridRetriever("RRF, k = 60", [keyword, dense], new ReciprocalRankFusion(60)).RetrieveAsync(new RetrievalRequest(question.Question, asker, 4))).Hits;
        Console.WriteLine("RRF, k = 60 (rank in keyword, rank in dense):");
        string At(int list, Hit hit) => ranks[list].TryGetValue(hit.Chunk.ReturnKey, out var r) ? r.ToString() : "-";
        for (var i = 0; i < fused.Count; i++) Console.WriteLine($"  {i + 1,2}  {fused[i].Score,7:0.0000}  ({At(0, fused[i]),2}, {At(1, fused[i]),2})  {Label(fused[i])}");
    }

    public static async Task Bm25Async(Lab lab)
    {
        double[] k1s = [0.5, 0.9, 1.2, 1.6, 2.0], bs = [0, 0.25, 0.5, 0.75, 1.0];
        Console.WriteLine($"Golden set v2, {lab.Split}: nDCG@4 and, in brackets, sufficient, by k1 and b; Chapter 9's analyzer, parent–child chunks");
        Console.WriteLine();
        Console.WriteLine($"{"k1 \\ b",-7}" + string.Join("", bs.Select(b => $"{b,13:0.00}")));
        foreach (var k1 in k1s)
        {
            var cells = new List<string>();
            foreach (var b in bs)
            {
                var run = await lab.RunAsync(lab.Bm25($"BM25 k1 {k1} b {b}", k1, b));
                cells.Add($"{run.Mean("nDCG"):0.00} ({run.Mean("Sufficient"):0.00})");
            }

            Console.WriteLine($"{k1,-7:0.0}" + string.Join("", cells.Select(c => $"{c,13}")));
        }

        var lengths = lab.Chunks.Select(c => SimpleAnalyzer.Terms(c.Chunk.IndexText).Count()).Order().ToList();
        Console.WriteLine($"Chunk lengths in terms: shortest {lengths[0]}, median {lengths[lengths.Count / 2]}, longest {lengths[^1]}");
    }

    public static async Task AnalyzersAsync(Lab lab)
    {
        var glossary = ContosoAnalyzers.Glossary(GlossaryPath);
        var analyzers = new (string Name, Analyzer Analyzer)[]
        {
            ("standard", new StandardAnalyzer(ContosoAnalyzers.Version)),
            ("english", new EnglishAnalyzer(ContosoAnalyzers.Version)),
            ("contoso", ContosoAnalyzers.Identifiers()),
            ("contoso + glossary", ContosoAnalyzers.Identifiers(glossary)),
        };
        const string sample = "Fit PN-4471-C seal kits after e217 resets (LOTO)?";
        Console.WriteLine($"\"{sample}\"");
        foreach (var (name, analyzer) in analyzers) Console.WriteLine($"  {name,-19} {string.Join(" ", ContosoAnalyzers.Terms(analyzer, sample))}");
        Console.WriteLine();

        var baseline = await lab.RunAsync(lab.Bm25());
        var runs = new List<RunRecord> { baseline };
        foreach (var (name, analyzer) in analyzers.Take(3))
        {
            using var lucene = new LuceneRetriever($"Lucene, {name} analyzer", lab.Chunks, analyzer);
            runs.Add(await lab.RunAsync(lucene));
        }

        using (var keyword = new LuceneRetriever("Lucene, contoso, glossary on queries", lab.Chunks, analyzers[2].Analyzer, analyzers[3].Analyzer))
        {
            runs.Add(await lab.RunAsync(keyword));
        }

        PrintRuns(lab, runs, baseline, "Chapter 9's BM25");
        var named = lab.Answerable.Where(q => Lab.HasIdentifier(q.Question)).Select(q => q.Id).ToHashSet();
        Console.WriteLine();
        Console.WriteLine($"{"Sufficient",-44} {"identifier",10} {"none",6}");
        foreach (var run in runs)
        {
            Console.WriteLine($"{run.Configuration,-44} {run.Answerable.Where(q => named.Contains(q.Id)).Average(q => q.Sufficient),10:F2} " +
                $"{run.Answerable.Where(q => !named.Contains(q.Id)).Average(q => q.Sufficient),6:F2}");
        }

        Console.WriteLine($"{"questions",-44} {named.Count,10} {lab.Answerable.Count() - named.Count,6}");
    }

    public static async Task FiltersAsync(Lab lab)
    {
        using var model = Lab.Bge();
        using var keyword = Keyword(lab);
        var dense = await lab.DenseAsync(model);
        var hybrid = new HybridRetriever("hybrid, RRF k = 60", [keyword, dense], new ReciprocalRankFusion(60));

        // Facets: how the matches for one question spread over the collections, before any filter.
        var asker = Contoso.Corpus.People.Find("kwame.asante")!;
        const string question = "How do I clear error E-217?";
        var matches = (await keyword.RetrieveAsync(new RetrievalRequest(question, asker, 200))).Hits;
        Console.WriteLine($"Facets for \"{question}\" ({matches.Count} matching parents, Kwame's permissions):");
        foreach (var facet in matches.GroupBy(h => h.Chunk.Collection).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {facet.Key,-24} {facet.Count(),3}   {string.Join(", ", facet.Select(h => h.Chunk.Chunk.Doc).Distinct().Take(4))}");
        }

        // An oracle filter: the collection of the question's first relevant document, what Chapter 13 must read from the question.
        var filters = lab.Answerable.ToDictionary(q => q.Question,
            q => new RetrievalFilter { Collections = [Lab.Collection(lab.Metadata[q.Relevant[0].Doc].DocumentType)] });
        Console.WriteLine();
        var runs = new List<RunRecord>();
        foreach (var retriever in new IRetriever[] { keyword, dense, hybrid })
        {
            var open = await lab.RunAsync(retriever);
            var filtered = await lab.RunAsync(retriever, filters);
            runs.Add(open);
            runs.Add(filtered with { Configuration = "  with the collection filter" });
        }

        Console.WriteLine($"{"Configuration",-44} {"Recall@4",8} {"nDCG@4",7} {"Suff.",6}   Sufficient against no filter");
        for (var i = 0; i < runs.Count; i += 2)
        {
            foreach (var run in runs.Skip(i).Take(2))
            {
                Console.WriteLine($"{run.Configuration,-44} {run.Mean("Recall"),8:F2} {run.Mean("nDCG"),7:F2} {run.Mean("Sufficient"),6:F2}   " +
                    (run == runs[i] ? "" : Lab.Interval(Lab.Compare(runs[i], run))));
            }
        }

        if (Environment.GetCommandLineArgs().Contains("--flips"))
        {
            // Which questions the filter changes, and the collections of their relevant documents.
            for (var i = 0; i < runs.Count; i += 2)
            {
                foreach (var (open, filtered) in runs[i].Answerable.Zip(runs[i + 1].Answerable).Where(p => p.First.Sufficient != p.Second.Sufficient))
                {
                    var flipped = lab.Questions.First(q => q.Id == open.Id);
                    var collections = flipped.Relevant.Select(r => Lab.Collection(lab.Metadata[r.Doc].DocumentType)).Distinct();
                    Console.WriteLine($"  {runs[i].Configuration}: {open.Id} {open.Sufficient:0} -> {filtered.Sufficient:0}; relevant in {string.Join(", ", collections)}");
                }
            }
        }
    }

    public static async Task FusionAsync(Lab lab)
    {
        using var model = Lab.Bge();
        using var keyword = Keyword(lab);
        var dense = await lab.DenseAsync(model);
        var fusions = new IFusion[]
        {
            new ReciprocalRankFusion(1), new ReciprocalRankFusion(10), new ReciprocalRankFusion(60), new ReciprocalRankFusion(100),
            new ReciprocalRankFusion(60, [2, 1]), new ReciprocalRankFusion(60, [1, 2]),
            new ConvexFusion(0.3), new ConvexFusion(0.5), new ConvexFusion(0.7), new ConvexFusion(0.5, Normalization.ZScore),
        };
        var (k, d) = (await lab.RunAsync(keyword), await lab.RunAsync(dense));
        Console.WriteLine($"Golden set v2, {lab.Split}: keyword (Lucene, Contoso analyzer) and dense (bge-small INT8), 20 candidates each, k = 4");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-40} {"Recall@4",8} {"nDCG@4",7} {"Suff.",6}   {"Against keyword",22}   {"Against dense",22}");
        foreach (var run in new[] { k, d })
        {
            Console.WriteLine($"{run.Configuration,-40} {run.Mean("Recall"),8:F2} {run.Mean("nDCG"),7:F2} {run.Mean("Sufficient"),6:F2}");
        }

        foreach (var fusion in fusions)
        {
            var run = await lab.RunAsync(new HybridRetriever(fusion.Name, [keyword, dense], fusion));
            Console.WriteLine($"{run.Configuration,-40} {run.Mean("Recall"),8:F2} {run.Mean("nDCG"),7:F2} {run.Mean("Sufficient"),6:F2}   " +
                $"{Lab.Interval(Lab.Compare(k, run)),22}   {Lab.Interval(Lab.Compare(d, run)),22}");
        }
    }

    public static async Task SpladeAsync(Lab lab)
    {
        var root = EmbeddingModels.Root;
        using var encoder = new SpladeEncoder(Path.Combine(root, "splade-pp-en-v1", "model.onnx"), Path.Combine(root, "splade-pp-en-v1", "vocab.txt"));
        var texts = lab.Chunks.Select(c => c.Chunk.IndexText).ToList();
        var stopwatch = Stopwatch.StartNew();
        var vectors = EncodeAll(encoder, texts);
        var encoding = stopwatch.Elapsed;
        var sparse = new SparseRetriever("SPLADE++", lab.Chunks, encoder, vectors);
        Console.WriteLine($"SPLADE++ (BERT base, FP32): {texts.Count} chunks encoded in {encoding.TotalSeconds:F1} s ({texts.Count / encoding.TotalSeconds:F1} a second, all cores); " +
            $"{vectors.Average(v => v.Count):F0} nonzero terms a chunk, {sparse.Postings:N0} postings");

        var example = lab.Questions.First(q => q.Id == "G002").Question;
        var weights = encoder.Encode([example])[0].OrderByDescending(w => w.Value).Take(14);
        Console.WriteLine($"\"{example}\" →");
        Console.WriteLine("  " + string.Join(" ", weights.Select(w => $"{encoder.Term(w.Key)} {w.Value:0.00}")));
        Console.WriteLine();

        using var model = Lab.Bge();
        using var keyword = Keyword(lab);
        var dense = await lab.DenseAsync(model);
        var rrf = new ReciprocalRankFusion(60);
        var baseline = await lab.RunAsync(keyword);
        var runs = new List<RunRecord> { baseline, await lab.RunAsync(sparse), await lab.RunAsync(dense) };
        foreach (var (name, sources) in new (string, IRetriever[])[]
        {
            ("RRF: keyword + dense", [keyword, dense]),
            ("RRF: SPLADE + dense", [sparse, dense]),
            ("RRF: keyword + SPLADE + dense", [keyword, sparse, dense]),
        })
        {
            runs.Add(await lab.RunAsync(new HybridRetriever(name, sources, rrf)));
        }

        PrintRuns(lab, runs, baseline, "keyword");
        PrintTypes(lab, runs);
    }

    public static async Task EvaluateAsync(Lab lab)
    {
        using var model = Lab.Bge();
        using var keyword = Keyword(lab);
        var dense = await lab.DenseAsync(model);
        var runs = new List<RunRecord>
        {
            await lab.RunAsync(lab.Bm25()),
            await lab.RunAsync(keyword),
            await lab.RunAsync(dense),
            await lab.RunAsync(new HybridRetriever("hybrid: RRF, k = 60", [keyword, dense], new ReciprocalRankFusion(60))),
            await lab.RunAsync(new HybridRetriever("hybrid: convex, alpha 0.5, z-score", [keyword, dense], new ConvexFusion(0.5, Normalization.ZScore))),
        };
        PrintRuns(lab, runs, runs[2], "dense");
        Console.WriteLine();
        Console.WriteLine($"RRF against Chapter 9's BM25: sufficient {Lab.Interval(Lab.Compare(runs[0], runs[3]))}; " +
            $"convex against RRF: sufficient {Lab.Interval(Lab.Compare(runs[3], runs[4]))}, nDCG@4 {Lab.Interval(Lab.Compare(runs[3], runs[4], "nDCG"))}");
        PrintTypes(lab, runs);
    }

    /// <summary>SPLADE in length-sorted batches of about 2,048 tokens: the model's output holds 30,522 scores for every token.</summary>
    private static IReadOnlyList<Dictionary<int, float>> EncodeAll(SpladeEncoder encoder, IReadOnlyList<string> texts)
    {
        var lengths = texts.Select(encoder.CountTokens).ToArray();
        var order = Enumerable.Range(0, texts.Count).OrderBy(i => lengths[i]).ToArray();
        var vectors = new Dictionary<int, float>[texts.Count];
        for (var next = 0; next < order.Length;)
        {
            var batch = new List<int> { order[next++] };
            while (next < order.Length && lengths[order[next]] * (batch.Count + 1) <= 2048) batch.Add(order[next++]);
            var encoded = encoder.Encode([.. batch.Select(i => texts[i])]);
            for (var j = 0; j < batch.Count; j++) vectors[batch[j]] = encoded[j];
        }

        return vectors;
    }

    private static void PrintRuns(Lab lab, IReadOnlyList<RunRecord> runs, RunRecord baseline, string against)
    {
        Console.WriteLine($"Golden set v2, {lab.Split}: {baseline.Answerable.Count()} answerable questions; k = 4");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-44} {"Recall@4",8} {"nDCG@4",7} {"Suff.",6}   Sufficient against {against}");
        foreach (var run in runs)
        {
            Console.WriteLine($"{run.Configuration,-44} {run.Mean("Recall"),8:F2} {run.Mean("nDCG"),7:F2} {run.Mean("Sufficient"),6:F2}   " +
                (run == baseline ? "" : Lab.Interval(Lab.Compare(baseline, run))));
        }
    }

    private static void PrintTypes(Lab lab, IReadOnlyList<RunRecord> runs)
    {
        var types = lab.Answerable.GroupBy(q => q.Type).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        Console.WriteLine();
        Console.WriteLine($"{"Sufficient by question type",-44} " + string.Join(" ", types.Select(t => $"{t,11}")));
        foreach (var run in runs)
        {
            Console.WriteLine($"{run.Configuration,-44} " + string.Join(" ", types.Select(t => $"{run.Answerable.Where(q => q.Type == t).Average(q => q.Sufficient),11:F2}")));
        }

        Console.WriteLine($"{"questions",-44} " + string.Join(" ", types.Select(t => $"{lab.Answerable.Count(q => q.Type == t),11}")));
    }

    private static string Trim(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string Rank(int? rank) => rank?.ToString() ?? "-";
}
