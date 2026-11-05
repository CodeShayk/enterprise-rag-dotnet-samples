using System.Diagnostics;
using System.Numerics.Tensors;
using Contoso.Corpus;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Retrieval;

namespace Ch12.Reranking.Cli;

public static class Commands
{
    /// <summary>The hybrid's top four against the same hybrid's 20 candidates re-ranked by each cross-encoder.</summary>
    public static async Task RerankAsync(Lab lab)
    {
        using var bge = Lab.Bge();
        var hybrid = await lab.HybridAsync(bge);
        var encoders = new[] { Lab.CrossEncoder("L6"), Lab.CrossEncoder("L6-int8"), Lab.CrossEncoder("L12") };
        var configurations = new List<IRetriever> { hybrid };
        configurations.AddRange(encoders.Select(e => new RerankingRetriever($"+ {e.Name}", hybrid, new MemoReranker(new OnnxCrossEncoderReranker(e)), 20)));

        Console.WriteLine($"Golden set v2, {lab.Split}: {lab.Answerable.Count()} answerable questions; 20 hybrid candidates re-ranked; passages up to 512 tokens");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-26} {"Suff@4",6} {"nDCG@10",7} {"MRR@10",6} {"p50 ms",7} {"p95 ms",7}   Sufficient against the hybrid");
        RunRecord? baseline = null;
        foreach (var retriever in configurations)
        {
            var (p50, p95) = await lab.LatencyAsync(retriever);
            var (at4, at10) = (await lab.RunAsync(retriever), await lab.RunAsync(retriever, 10));
            baseline ??= at4;
            Console.WriteLine($"{retriever.Name,-26} {at4.Mean("Sufficient"),6:F2} {at10.Mean("nDCG"),7:F2} {at10.Mean("MRR"),6:F2} {p50,7:F1} {p95,7:F1}   " +
                (at4 == baseline ? "" : Lab.Interval(Lab.Compare(baseline, at4))));
        }

        foreach (var encoder in encoders) encoder.Dispose();
    }

    /// <summary>How many candidates to re-rank: quality and latency from 4 to 40.</summary>
    public static async Task CandidatesAsync(Lab lab, string which)
    {
        using var bge = Lab.Bge();
        var hybrid = await lab.HybridAsync(bge, perSource: 40);
        using var encoder = Lab.CrossEncoder(which);
        var reranker = new OnnxCrossEncoderReranker(encoder);
        Console.WriteLine($"Golden set v2, {lab.Split}: {encoder.Name} re-ranking the hybrid's first N parents (40 from each side)");
        Console.WriteLine();
        Console.WriteLine($"{"Candidates",-11} {"Suff@4",6} {"nDCG@10",7} {"MRR@10",6} {"p50 ms",7} {"p95 ms",7}");
        var baseline = await lab.RunAsync(hybrid);
        var baseline10 = await lab.RunAsync(hybrid, 10);
        var (b50, b95) = await lab.LatencyAsync(hybrid);
        Console.WriteLine($"{"none",-11} {baseline.Mean("Sufficient"),6:F2} {baseline10.Mean("nDCG"),7:F2} {baseline10.Mean("MRR"),6:F2} {b50,7:F1} {b95,7:F1}");
        foreach (var n in new[] { 4, 8, 12, 20, 30, 40 })
        {
            var retriever = new RerankingRetriever($"{n} candidates", hybrid, new MemoReranker(reranker), n);
            var (p50, p95) = await lab.LatencyAsync(retriever);
            var (at4, at10) = (await lab.RunAsync(retriever), await lab.RunAsync(retriever, 10));
            Console.WriteLine($"{n,-11} {at4.Mean("Sufficient"),6:F2} {at10.Mean("nDCG"),7:F2} {at10.Mean("MRR"),6:F2} {p50,7:F1} {p95,7:F1}");
        }
    }

    /// <summary>What the cross-encoder reads: the parent cut to 128, 256 or 512 tokens, or only the matched child.</summary>
    public static async Task TruncateAsync(Lab lab, string which)
    {
        using var bge = Lab.Bge();
        var hybrid = await lab.HybridAsync(bge);
        Console.WriteLine($"Golden set v2, {lab.Split}: {which}, 20 candidates; tokens per question-passage pair");
        Console.WriteLine();
        Console.WriteLine($"{"Passage",-30} {"Suff@4",6} {"nDCG@10",7} {"p50 ms",7} {"p95 ms",7}");
        foreach (var (label, tokens, child) in new[] { ("parent, 128 tokens", 128, false), ("parent, 256 tokens", 256, false), ("parent, 512 tokens", 512, false), ("matched child only, 512", 512, true) })
        {
            using var encoder = Lab.CrossEncoder(which, tokens);
            var reranker = new MemoReranker(new OnnxCrossEncoderReranker(encoder, child ? c => c.Chunk.IndexText : null));
            var retriever = new RerankingRetriever(label, hybrid, reranker, 20);
            var (p50, p95) = await lab.LatencyAsync(retriever);
            var (at4, at10) = (await lab.RunAsync(retriever), await lab.RunAsync(retriever, 10));
            Console.WriteLine($"{label,-30} {at4.Mean("Sufficient"),6:F2} {at10.Mean("nDCG"),7:F2} {p50,7:F1} {p95,7:F1}");
        }
    }

    /// <summary>MMR and a per-document cap after re-ranking: sufficiency against how varied the top four are.</summary>
    public static async Task DiversityAsync(Lab lab, string which)
    {
        using var bge = Lab.Bge();
        var hybrid = await lab.HybridAsync(bge);
        using var encoder = Lab.CrossEncoder(which);
        var reranker = new MemoReranker(new OnnxCrossEncoderReranker(encoder));
        // Each candidate's evidence embedded once, for the similarity MMR subtracts.
        var passages = new OnnxEmbeddingGenerator(bge);
        var embedded = await passages.GenerateAsync(lab.Chunks.Select(OnnxCrossEncoderReranker.Evidence));
        var vectors = embedded.Select(e => e.Vector.ToArray()).ToArray();
        float[] Vector(IndexedChunk c) => vectors[c.Ordinal];

        var configurations = new (string Name, IReranker Reranker)[]
        {
            ("re-ranked, no diversity", reranker),
            ("MMR, lambda 0.8", new MaximalMarginalRelevance(reranker, Vector, 0.8)),
            ("MMR, lambda 0.6", new MaximalMarginalRelevance(reranker, Vector, 0.6)),
            ("MMR, lambda 0.4", new MaximalMarginalRelevance(reranker, Vector, 0.4)),
            ("at most 2 per document", new MaximalMarginalRelevance(reranker, Vector, 1.0, 2)),
            ("near-duplicates at 0.95", new MaximalMarginalRelevance(reranker, Vector, 1.0, int.MaxValue, 0.95)),
        };
        Console.WriteLine($"Golden set v2, {lab.Split}: {encoder.Name} on 20 candidates, then diversity; similarity is bge-small cosine between evidence texts");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-26} {"Suff@4",6} {"nDCG@10",7} {"Documents",9} {"Similarity",10}   Sufficient against no diversity");
        RunRecord? baseline = null;
        foreach (var (name, r) in configurations)
        {
            var retriever = new RerankingRetriever(name, hybrid, r, 20);
            var (at4, at10) = (await lab.RunAsync(retriever), await lab.RunAsync(retriever, 10));
            baseline ??= at4;
            var (documents, similarity) = (new List<double>(), new List<double>());
            foreach (var question in lab.Answerable)
            {
                var hits = (await retriever.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, 4))).Hits;
                documents.Add(hits.Select(h => h.Chunk.Chunk.Doc).Distinct().Count());
                var pairs = hits.SelectMany((a, i) => hits.Skip(i + 1).Select(b => (double)TensorPrimitives.CosineSimilarity(Vector(a.Chunk), Vector(b.Chunk)))).ToList();
                if (pairs.Count > 0) similarity.Add(pairs.Average());
            }

            Console.WriteLine($"{name,-26} {at4.Mean("Sufficient"),6:F2} {at10.Mean("nDCG"),7:F2} {documents.Average(),9:F2} {similarity.Average(),10:F3}   " +
                (at4 == baseline ? "" : Lab.Interval(Lab.Compare(baseline, at4))));
        }
    }

    /// <summary>
    /// Platt scaling fitted on dev candidates and checked on test: reliability by probability band, and what a
    /// threshold does to the evidence and to the questions that should end in "I don't know".
    /// </summary>
    public static async Task CalibrateAsync(string which)
    {
        var (dev, test) = (await Lab.LoadAsync("dev"), await Lab.LoadAsync("test"));
        using var bge = Lab.Bge();
        using var encoder = Lab.CrossEncoder(which);
        var reranker = new OnnxCrossEncoderReranker(encoder);

        async Task<List<(GoldenQuestion Question, IReadOnlyList<(Hit Hit, bool Relevant)> Scored)>> ScoreAsync(Lab lab)
        {
            var hybrid = await lab.HybridAsync(bge);
            var results = new List<(GoldenQuestion, IReadOnlyList<(Hit, bool)>)>();
            foreach (var question in lab.Questions)
            {
                var asker = People.Find(question.Asker)!;
                var candidates = (await hybrid.RetrieveAsync(new RetrievalRequest(question.Question, asker, 20))).Hits;
                var ranked = await reranker.RerankAsync(question.Question, candidates, candidates.Count);
                results.Add((question, [.. ranked.Select(h => (h, lab.Relevant(question, h)))]));
            }

            return results;
        }

        var (devScored, testScored) = (await ScoreAsync(dev), await ScoreAsync(test));
        var calibration = Calibration.Fit([.. devScored.Where(r => r.Question.Answerable).SelectMany(r => r.Scored.Select(s => ((double)s.Hit.Score, s.Relevant)))]);
        Console.WriteLine($"{encoder.Name}: Platt scaling fitted on {devScored.Where(r => r.Question.Answerable).Sum(r => r.Scored.Count)} dev candidates: " +
            $"P(relevant) = sigmoid({calibration.A:0.000} × logit {(calibration.B < 0 ? "-" : "+")} {Math.Abs(calibration.B):0.000})");
        Console.WriteLine();
        Console.WriteLine("Test candidates by predicted probability");
        Console.WriteLine($"{"Band",-11} {"Candidates",10} {"Predicted",9} {"Relevant",8}");
        var samples = testScored.Where(r => r.Question.Answerable).SelectMany(r => r.Scored).Select(s => (P: calibration.Probability(s.Hit.Score), s.Relevant)).ToList();
        foreach (var (low, high) in new[] { (0.0, 0.1), (0.1, 0.3), (0.3, 0.5), (0.5, 0.7), (0.7, 0.9), (0.9, 1.01) })
        {
            var band = samples.Where(s => s.P >= low && s.P < high).ToList();
            if (band.Count == 0) continue;
            Console.WriteLine($"{$"{low:0.0}–{Math.Min(high, 1):0.0}",-11} {band.Count,10} {band.Average(s => s.P),9:F2} {band.Average(s => s.Relevant ? 1.0 : 0),8:F2}");
        }

        var ece = samples.GroupBy(s => Math.Min(9, (int)(s.P * 10))).Sum(g => g.Count() * Math.Abs(g.Average(s => s.P) - g.Average(s => s.Relevant ? 1.0 : 0))) / samples.Count;
        Console.WriteLine($"Expected calibration error {ece:F3}; Brier score {samples.Average(s => Math.Pow(s.P - (s.Relevant ? 1 : 0), 2)):F3}");
        Console.WriteLine();

        // A threshold on the best candidate: answerable questions should clear it, unanswerable ones shouldn't.
        var all = devScored.Concat(testScored).ToList();
        var (answerable, unanswerable) = (all.Where(r => r.Question.Answerable).ToList(), all.Where(r => !r.Question.Answerable).ToList());
        Console.WriteLine($"Threshold on the best candidate, dev and test ({answerable.Count} answerable, {unanswerable.Count} that should end in \"I don't know\")");
        Console.WriteLine($"{"P(relevant) ≥",-14} {"Answerable kept",15} {"Abstentions caught",18} {"Evidence kept",13}");
        foreach (var threshold in new[] { 0.1, 0.3, 0.5, 0.7 })
        {
            double Best((GoldenQuestion, IReadOnlyList<(Hit Hit, bool)> Scored) r) => r.Scored.Count == 0 ? 0 : calibration.Probability(r.Scored[0].Hit.Score);
            var kept = answerable.Count(r => Best(r) >= threshold);
            var caught = unanswerable.Count(r => Best(r) < threshold);
            var evidence = answerable.Average(r => r.Scored.Take(4).Count(s => calibration.Probability(s.Hit.Score) >= threshold));
            Console.WriteLine($"{threshold,-14:0.0} {$"{kept} of {answerable.Count}",15} {$"{caught} of {unanswerable.Count}",18} {evidence,13:F2}");
        }

        Console.WriteLine();
        foreach (var r in unanswerable)
        {
            var best = r.Scored.Count == 0 ? 0 : calibration.Probability(r.Scored[0].Hit.Score);
            Console.WriteLine($"  {r.Question.Id,-8} {Trim(r.Question.Question, 58),-58} best {best:0.00}");
        }

        // The same thresholds by split: a threshold is chosen on dev and reported on test.
        Console.WriteLine();
        Console.WriteLine($"{"P(relevant) ≥",-14} {"Dev kept",12} {"Dev caught",11} {"Test kept",12} {"Test caught",12}");
        foreach (var threshold in new[] { 0.1, 0.3, 0.5, 0.7 })
        {
            double Best((GoldenQuestion, IReadOnlyList<(Hit Hit, bool)> Scored) r) => r.Scored.Count == 0 ? 0 : calibration.Probability(r.Scored[0].Hit.Score);
            string Kept(IEnumerable<(GoldenQuestion Question, IReadOnlyList<(Hit Hit, bool)> Scored)> rows) { var a = rows.Where(r => r.Question.Answerable).ToList(); return $"{a.Count(r => Best(r) >= threshold)} of {a.Count}"; }
            string Caught(IEnumerable<(GoldenQuestion Question, IReadOnlyList<(Hit Hit, bool)> Scored)> rows) { var u = rows.Where(r => !r.Question.Answerable).ToList(); return $"{u.Count(r => Best(r) < threshold)} of {u.Count}"; }
            Console.WriteLine($"{threshold,-14:0.0} {Kept(devScored),12} {Caught(devScored),11} {Kept(testScored),12} {Caught(testScored),12}");
        }
    }

    /// <summary>One question's candidates before and after re-ranking, with calibrated probabilities if a calibration is given.</summary>
    public static async Task ExplainAsync(Lab lab, string id, string which)
    {
        using var bge = Lab.Bge();
        var hybrid = await lab.HybridAsync(bge);
        using var encoder = Lab.CrossEncoder(which);
        var reranker = new OnnxCrossEncoderReranker(encoder);
        var question = lab.Questions.First(q => q.Id == id);
        var candidates = (await hybrid.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, 20))).Hits;
        var ranked = await reranker.RerankAsync(question.Question, candidates, 6);
        Console.WriteLine($"{question.Id} ({question.Asker}): {question.Question}");
        Console.WriteLine($"Re-ranked by {encoder.Name} (hybrid rank → logit):");
        foreach (var hit in ranked)
        {
            var before = candidates.ToList().FindIndex(c => c.Chunk.ReturnKey == hit.Chunk.ReturnKey) + 1;
            var header = hit.Chunk.Chunk.Header?.Split(" › ").Last() ?? "";
            Console.WriteLine($"  {before,2} → {hit.Score,6:0.00}  {hit.Chunk.Chunk.Doc,-16} {Trim(header, 40),-40} {(lab.Relevant(question, hit) ? "relevant" : "")}");
        }
    }

    /// <summary>The chosen configuration against the hybrid alone.</summary>
    public static async Task EvaluateAsync(Lab lab, string which, int candidates, int tokens)
    {
        using var bge = Lab.Bge();
        var hybrid = await lab.HybridAsync(bge);
        using var encoder = Lab.CrossEncoder(which, tokens);
        var reranked = new RerankingRetriever($"+ {encoder.Name}, {candidates} candidates, {tokens} tokens", hybrid, new MemoReranker(new OnnxCrossEncoderReranker(encoder)), candidates);
        var runs = new List<(IRetriever Retriever, RunRecord At4, RunRecord At10, double P95)>();
        foreach (var retriever in new IRetriever[] { hybrid, reranked })
        {
            var (_, p95) = await lab.LatencyAsync(retriever);
            runs.Add((retriever, await lab.RunAsync(retriever), await lab.RunAsync(retriever, 10), p95));
        }

        var audited = lab.Questions.Count(q => q.Relevant.Any(r => r.Text.Contains(" means ")) && q.Relevant.Any(r => r.Text.StartsWith('|')));
        Console.WriteLine($"Golden set v2, {lab.Split}: {lab.Answerable.Count()} answerable questions" + (audited > 0 ? $"; labels audited for {audited} code-table questions" : ""));
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-46} {"Suff@4",6} {"Recall@4",8} {"nDCG@10",7} {"MRR@10",6} {"p95 ms",7}");
        foreach (var (retriever, at4, at10, p95) in runs)
        {
            Console.WriteLine($"{retriever.Name,-46} {at4.Mean("Sufficient"),6:F2} {at4.Mean("Recall"),8:F2} {at10.Mean("nDCG"),7:F2} {at10.Mean("MRR"),6:F2} {p95,7:F1}");
        }

        var (h, r) = (runs[0], runs[1]);
        // The questions re-ranking changed, and what each top four holds afterwards.
        foreach (var (before, after) in h.At4.Answerable.Zip(r.At4.Answerable).Where(p => p.First.Sufficient != p.Second.Sufficient))
        {
            var question = lab.Questions.First(q => q.Id == before.Id);
            var top = (await reranked.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, 4))).Hits;
            Console.WriteLine($"  {before.Id,-8} {before.Sufficient:0} -> {after.Sufficient:0}  {Trim(question.Question, 50),-50}  " +
                string.Join(" | ", top.Select(t => $"{t.Chunk.Chunk.Doc}: {Trim(t.Chunk.Chunk.Header?.Split(" › ").Last() ?? "", 24)}")));
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"Re-ranked against the hybrid: sufficient {Lab.Interval(Lab.Compare(h.At4, r.At4))}, nDCG@10 {Lab.Interval(Lab.Compare(h.At10, r.At10, "nDCG"))}, " +
            $"MRR@10 {Lab.Interval(Lab.Compare(h.At10, r.At10, "MRR"))}");
    }

    internal static string Trim(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
