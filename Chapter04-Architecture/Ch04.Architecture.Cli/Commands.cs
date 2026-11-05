using Ch04.Knowledge.Adapters;
using Ch04.Knowledge.Core;
using Ch04.Knowledge.Core.Ports;
using Contoso.Corpus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;

namespace Ch04.Architecture.Cli;

public static class Commands
{
    public static async Task AskAsync(IServiceProvider services, string question, string userId)
    {
        var asker = People.Find(userId) ?? throw new ArgumentException($"Unknown user '{userId}'.");
        var answer = await services.GetRequiredService<RagPipeline>().AskAsync(question, asker);

        Console.WriteLine($"{asker.Name}: {question}");
        for (var i = 0; i < answer.Evidence.Count; i++)
        {
            Console.WriteLine($"  [{i + 1}] {answer.Evidence[i].Chunk.Id,-24} score {answer.Evidence[i].Score:F3}");
        }

        await foreach (var text in answer.Text)
        {
            Console.Write(text);
        }

        Console.WriteLine();
    }

    public static async Task BudgetAsync(IServiceProvider services, IConfiguration configuration)
    {
        var (budget, rates, demand, prices) = BudgetMath.Read(configuration);
        var retriever = services.GetRequiredService<IRetriever>();
        var tokenizer = services.GetRequiredService<Tokenizer>();
        var questions = SmokeSet.Load();

        Console.WriteLine($"Targets (p95):    first token {budget.FirstTokenSloMs:N0} ms, whole answer {budget.AnswerSloMs:N0} ms");
        Console.WriteLine($"Fixed stages:     {string.Join(", ", budget.StageMs.Select(s => $"{s.Key} {s.Value}"))} = {budget.FixedStagesMs} ms");
        Console.WriteLine($"Measured rates:   {rates.Source}");
        Console.WriteLine($"                  prefill {rates.PrefillTokensPerSecond} tokens/s, decode {rates.DecodeTokensPerSecond} tokens/s, {rates.ThreadsPerAnswer} threads");
        Console.WriteLine($"Prompt allowance: {BudgetMath.PromptTokenAllowance(budget, rates)} model tokens before the first-token target is missed");
        Console.WriteLine();
        Console.WriteLine("Evidence  Prompt tokens  First token  Whole answer  Meets targets");

        double promptAtTop = 0, answerAtTop = 0;
        for (var k = 0; k <= 4; k++)
        {
            // The mean prompt over the smoke set, counted on the messages the generator would send.
            var sizes = new List<int>();
            foreach (var question in questions)
            {
                IReadOnlyList<RetrievedChunk> evidence = k == 0 ? [] : await retriever.RetrieveAsync(question.Question, People.Find(question.Asker)!, k);
                var messages = ChatAnswerGenerator.BuildMessages(question.Question, evidence);
                sizes.Add(tokenizer.CountTokens(string.Join("\n", messages.Select(m => m.Text))));
            }

            var prompt = sizes.Average();
            var first = BudgetMath.FirstTokenSeconds(budget, rates, prompt);
            var whole = BudgetMath.AnswerSeconds(budget, rates, prompt);
            var meets = first * 1000 <= budget.FirstTokenSloMs && whole * 1000 <= budget.AnswerSloMs;
            Console.WriteLine($"{(k == 0 ? "none" : $"top {k}"),-9} {prompt,13:F0} {first,10:F1} s {whole,11:F1} s  {(meets ? "yes" : "no")}");
            (promptAtTop, answerAtTop) = (prompt, whole);
        }

        var peakThreads = BudgetMath.PeakThreads(demand, rates, answerAtTop);
        Console.WriteLine();
        Console.WriteLine("At top 4, the skeleton's setting:");
        Console.WriteLine($"  Cost per 1,000 answers, metered model API at the example prices: ${BudgetMath.MeteredCostPerThousand(prices, rates, promptAtTop):F2}");
        Console.WriteLine($"  Cost per 1,000 answers, own CPUs at the example core-hour price:  ${BudgetMath.CpuCostPerThousand(prices, rates, answerAtTop):F2}");
        Console.WriteLine($"  Peak hour: {demand.Employees * demand.QuestionsPerEmployeePerDay * demand.PeakHourShare:N0} answers keep {peakThreads:F0} threads busy; " +
                          $"at {demand.TargetUtilization:P0} utilization, plan for {Math.Ceiling(peakThreads / demand.TargetUtilization):F0}");
    }
}
