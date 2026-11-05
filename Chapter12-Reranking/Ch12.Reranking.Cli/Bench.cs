using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Retrieval;
using Microsoft.ML.Tokenizers;

namespace Ch12.Reranking.Cli;

/// <summary>Where a re-ranking call spends its time: tokenizing the pairs or running the model.</summary>
public static class Bench
{
    public static async Task RunAsync(Lab lab, string which)
    {
        using var bge = Lab.Bge();
        var hybrid = await lab.HybridAsync(bge);
        using var encoder = Lab.CrossEncoder(which);
        var tokenizer = BertTokenizer.Create(Path.Combine(Contoso.Knowledge.Embeddings.EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"),
            new BertOptions { LowerCaseBeforeTokenization = true, RemoveNonSpacingMarks = true });
        var (tokenize, score) = (new List<double>(), new List<double>());
        foreach (var question in lab.Answerable.Take(40))
        {
            var candidates = (await hybrid.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, 20))).Hits;
            var passages = candidates.Select(c => OnnxCrossEncoderReranker.Evidence(c.Chunk)).ToList();
            var stopwatch = Stopwatch.StartNew();
            foreach (var p in passages) tokenizer.EncodeToIds(p, 500, addSpecialTokens: false, out _, out _);
            tokenize.Add(stopwatch.Elapsed.TotalMilliseconds);
            stopwatch.Restart();
            encoder.Score(question.Question, passages);
            score.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"40 questions, 20 candidates each: tokenizing the passages {tokenize.Average():F1} ms, the whole Score call {score.Average():F1} ms on average");

        // The same calls with the session limited to a few threads, on short and on full-length passages.
        var sample = lab.Answerable.Take(20).ToList();
        var sets = new List<(string Question, List<string> Parents, List<string> Children)>();
        foreach (var question in sample)
        {
            var candidates = (await hybrid.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, 20))).Hits;
            sets.Add((question.Question, [.. candidates.Select(c => OnnxCrossEncoderReranker.Evidence(c.Chunk))], [.. candidates.Select(c => c.Chunk.Chunk.IndexText)]));
        }

        // Inside the pipeline: retrieval with bge-small, then the cross-encoder, question after question.
        var ce = Path.Combine(Contoso.Knowledge.Embeddings.EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx");
        var ceVocab = Path.Combine(Contoso.Knowledge.Embeddings.EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt");
        foreach (var (bgeSpins, ceSpins) in new[] { (true, true), (false, true), (true, false), (false, false) })
        {
            using var embedder = new Contoso.Knowledge.Embeddings.OnnxEmbeddingModel(Contoso.Knowledge.Embeddings.EmbeddingModels.BgeSmall(int8: true), 0, bgeSpins);
            var pipelineHybrid = await lab.HybridAsync(embedder);
            using var crossEncoder = new OnnxCrossEncoder("L6", ce, ceVocab, 512, 0, ceSpins);
            var (retrieve, rerank) = (new List<double>(), new List<double>());
            foreach (var question in lab.Answerable.Take(40))
            {
                var stopwatch = Stopwatch.StartNew();
                var candidates = (await pipelineHybrid.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, 20))).Hits;
                retrieve.Add(stopwatch.Elapsed.TotalMilliseconds);
                stopwatch.Restart();
                crossEncoder.Score(question.Question, [.. candidates.Select(c => OnnxCrossEncoderReranker.Evidence(c.Chunk))]);
                rerank.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            Console.WriteLine($"spinning: bge-small {(bgeSpins ? "on " : "off")}, cross-encoder {(ceSpins ? "on " : "off")}   retrieval {retrieve.Average(),6:F1} ms   re-ranking {rerank.Average(),7:F1} ms");
        }

        var model = Path.Combine(Contoso.Knowledge.Embeddings.EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx");
        var vocab = Path.Combine(Contoso.Knowledge.Embeddings.EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt");
        foreach (var threads in new[] { 1, 4, 6, 14, 0 })
        {
            using var limited = new OnnxCrossEncoder($"{threads} threads", model, vocab, 512, threads);
            var (parents, children) = (new List<double>(), new List<double>());
            foreach (var (question, parentTexts, childTexts) in sets)
            {
                var stopwatch = Stopwatch.StartNew();
                limited.Score(question, parentTexts);
                parents.Add(stopwatch.Elapsed.TotalMilliseconds);
                stopwatch.Restart();
                limited.Score(question, childTexts);
                children.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            Console.WriteLine($"intra-op threads {(threads == 0 ? "default" : threads.ToString()),-8} parents {parents.Average(),7:F1} ms   children {children.Average(),7:F1} ms");
        }
    }
}
