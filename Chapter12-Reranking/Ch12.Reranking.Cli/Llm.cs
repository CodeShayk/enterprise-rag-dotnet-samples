using System.Diagnostics;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Ch12.Reranking.Cli;

/// <summary>
/// A pointwise LLM re-ranker for comparison: a local model grades each candidate from 0 to 3 for the question, one
/// call per candidate, and the candidates are sorted by grade, ties kept in the hybrid's order.
/// </summary>
public sealed class LlmPointwiseReranker(IChatClient chat, string name) : IReranker
{
    public string Name => name;

    public int Calls { get; private set; }

    public async Task<IReadOnlyList<Hit>> RerankAsync(string question, IReadOnlyList<Hit> candidates, int top, CancellationToken cancellationToken = default)
    {
        var graded = new List<(Hit Hit, int Grade, int Rank)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var passage = OnnxCrossEncoderReranker.Evidence(candidates[i].Chunk);
            var prompt = $"""
                Question: {question}

                Passage:
                {(passage.Length > 2000 ? passage[..2000] : passage)}

                How well does the passage answer the question? Reply with one digit only:
                0 = unrelated, 1 = related but no answer, 2 = answers part of it, 3 = answers it.
                """;
            var response = await chat.GetResponseAsync(prompt, new ChatOptions { Temperature = 0, MaxOutputTokens = 3 }, cancellationToken);
            Calls++;
            var digit = response.Text.FirstOrDefault(char.IsDigit);
            graded.Add((candidates[i], digit == default ? 0 : digit - '0', i));
        }

        return [.. graded.OrderByDescending(g => g.Grade).ThenBy(g => g.Rank).Take(top).Select(g => g.Hit with { Score = g.Grade, Source = Name })];
    }
}

public static class Llm
{
    public static async Task RunAsync(Lab lab, int count, string which)
    {
        // The first questions of the split, so the LLM's cost stays within minutes on a CPU.
        var subset = new Lab
        {
            Documents = lab.Documents, Metadata = lab.Metadata, Chunks = lab.Chunks, Harness = lab.Harness, Split = lab.Split,
            Questions = [.. lab.Answerable.Take(count)],
        };
        using var bge = Lab.Bge();
        var hybrid = await subset.HybridAsync(bge);
        using var encoder = Lab.CrossEncoder(which);
        var chat = new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = "llama3.2", OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 4096))
            .Build();
        var llm = new LlmPointwiseReranker(chat, "+ llama3.2, pointwise");
        var configurations = new IRetriever[]
        {
            hybrid,
            new RerankingRetriever($"+ {encoder.Name}", hybrid, new OnnxCrossEncoderReranker(encoder), 10),
            new RerankingRetriever(llm.Name, hybrid, llm, 10),
        };

        Console.WriteLine($"Golden set v2, {lab.Split}: the first {subset.Questions.Count} answerable questions; 10 hybrid candidates each; llama3.2 (3B) on CPU through Ollama");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-28} {"Suff@4",6} {"nDCG@4",6} {"Seconds a question",18}");
        foreach (var retriever in configurations)
        {
            var stopwatch = Stopwatch.StartNew();
            var run = await subset.RunAsync(retriever);
            var seconds = stopwatch.Elapsed.TotalSeconds / subset.Questions.Count;
            Console.WriteLine($"{retriever.Name,-28} {run.Mean("Sufficient"),6:F2} {run.Mean("nDCG"),6:F2} {seconds,18:F2}");
        }

        Console.WriteLine($"LLM calls: {llm.Calls}, one per candidate");
    }
}
