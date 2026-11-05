using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using static System.FormattableString;

namespace Ch21.Performance.Cli;

/// <summary>Where an answer's time goes, and what the re-ranker's ONNX Runtime settings change.</summary>
public static class Profile
{
    /// <summary>
    /// Every golden question through the shipped pipeline: each stage before the model timed here, the model's
    /// prefill and decode as recorded in Chapter 14's live runs.
    /// </summary>
    public static async Task ProfileAsync(Lab lab)
    {
        var generator = await lab.GeneratorAsync(Lab.AnswerChat(Lab.Large));
        var hybrid = await lab.HybridAsync();
        var reranker = new OnnxCrossEncoderReranker(lab.CrossEncoder);
        var assembler = new ContextAssembler(Lab.Calibration, Lab.O200k, new ContextOptions { MaxBlocks = 4 });
        var compressor = new ExtractiveCompressor(lab.CrossEncoder, Lab.O200k, 500);
        var rows = new List<(double Retrieve, double Rerank, double Compress, double Prefill, double Decode, long Prompt, long Output, bool Model)>();
        foreach (var q in lab.Questions)
        {
            var asker = People.Find(q.Asker)!;
            var clock = Stopwatch.StartNew();
            var candidates = (await hybrid.RetrieveAsync(new RetrievalRequest(q.Question, asker, 8))).Hits;
            var retrieve = clock.Elapsed.TotalSeconds;
            clock.Restart();
            var top = await reranker.RerankAsync(q.Question, candidates, 4);
            var rerank = clock.Elapsed.TotalSeconds;
            clock.Restart();
            var context = assembler.Assemble(top);
            if (!context.IsEmpty) compressor.Compress(q.Question, context);
            var compress = clock.Elapsed.TotalSeconds;

            var answer = await Lab.AnswerAsync(generator, q);
            var t = answer.Timings;
            var prompt = GroundingPrompt.Build(q.Question, asker, answer.Evidence, new DateOnly(2027, 2, 1)).Sum(m => (long)Lab.O200k.CountTokens(m.Text));
            rows.Add((retrieve, rerank, compress, (t.FirstTokenMs - t.RetrievalMs) / 1000, (t.TotalMs - t.FirstTokenMs) / 1000, prompt, t.OutputTokens, t.PromptTokens > 0));
        }

        var model = rows.Where(r => r.Model).ToList();
        Console.WriteLine($"{lab.Questions.Count} questions, {model.Count} reached the model (llama3.2 3B on the CPU, as recorded)");
        Console.WriteLine($"{"Stage",-34} {"p50",8} {"p95",8}");
        void Row(string name, IEnumerable<double> seconds) =>
            Console.WriteLine(Invariant($"{name,-34} {Stats.Percentile(seconds, 0.5),7:F2}s {Stats.Percentile(seconds, 0.95),7:F2}s"));
        Row("hybrid retrieval, 8 candidates", rows.Select(r => r.Retrieve));
        Row("re-ranking (MiniLM-L6)", rows.Select(r => r.Rerank));
        Row("assembly and compression", rows.Select(r => r.Compress));
        Row("prefill: prompt to first token", model.Select(r => r.Prefill));
        Row("decode: first token to last", model.Select(r => r.Decode));
        var first = model.Select(r => r.Retrieve + r.Rerank + r.Compress + r.Prefill).ToList();
        var answers = model.Select(r => r.Retrieve + r.Rerank + r.Compress + r.Prefill + r.Decode).ToList();
        Row("time to first token", first);
        Row("answer", answers);
        Console.WriteLine(Invariant($"prompt p50 {Stats.Percentile(model.Select(r => (double)r.Prompt), 0.5):F0} tokens (o200k), output p50 {Stats.Percentile(model.Select(r => (double)r.Output), 0.5):F0}"));
        Console.WriteLine(Invariant($"prefill {model.Sum(r => r.Prefill) / model.Sum(r => r.Prompt) * 1000:F1} s per 1,000 prompt tokens; decode {model.Sum(r => (double)r.Output) / model.Sum(r => r.Decode):F1} tokens a second"));
        Console.WriteLine(Invariant($"Chapter 4's targets: first token within 3 s {first.Count(f => f <= 3)} of {model.Count}; answer within 15 s {answers.Count(a => a <= 15)} of {model.Count}"));
        Console.WriteLine(Invariant($"the model's share of answer time: {model.Sum(r => r.Prefill + r.Decode) / answers.Sum():P0}"));
    }

    /// <summary>The re-ranker's latency for eight candidates under different thread counts and batch sizes.</summary>
    public static async Task OnnxAsync(Lab lab, int count = 40)
    {
        var hybrid = await lab.HybridAsync();
        var work = new List<(string Question, string[] Passages)>();
        foreach (var q in lab.Split("dev").Take(count))
        {
            var hits = (await hybrid.RetrieveAsync(new RetrievalRequest(q.Question, People.Find(q.Asker)!, 8))).Hits;
            work.Add((q.Question, [.. hits.Select(h => OnnxCrossEncoderReranker.Evidence(h.Chunk))]));
        }

        Console.WriteLine($"Re-ranking 8 candidates for {work.Count} dev questions: ms per question, p50 (p95)");
        Console.WriteLine($"{"intra-op threads",-18} {"batch of 1",16} {"batch of 8",16}");
        foreach (var threads in new[] { 1, 2, 4, 8, 0 })
        {
            using var encoder = Lab.CreateCrossEncoder(threads);
            var cells = new List<string>();
            foreach (var batch in new[] { 1, 8 })
            {
                encoder.Score(work[0].Question, work[0].Passages, batch);
                var ms = work.Select(w => Time(() => encoder.Score(w.Question, w.Passages, batch))).ToList();
                cells.Add(Invariant($"{Stats.Percentile(ms, 0.5),7:F0} ({Stats.Percentile(ms, 0.95),4:F0})"));
            }

            Console.WriteLine($"{(threads == 0 ? "default (all)" : threads.ToString()),-18} {cells[0],16} {cells[1],16}");
        }

        var clock = Stopwatch.StartNew();
        using var cold = Lab.CreateCrossEncoder();
        var created = clock.Elapsed.TotalMilliseconds;
        var first = Time(() => cold.Score(work[1].Question, work[1].Passages));
        var warm = Stats.Percentile(work.Select(w => Time(() => cold.Score(w.Question, w.Passages))), 0.5);
        Console.WriteLine(Invariant($"cold start: session {created:F0} ms, first call {first:F0} ms; warm call p50 {warm:F0} ms"));
    }

    private static double Time(Action action)
    {
        var clock = Stopwatch.StartNew();
        action();
        return clock.Elapsed.TotalMilliseconds;
    }
}
