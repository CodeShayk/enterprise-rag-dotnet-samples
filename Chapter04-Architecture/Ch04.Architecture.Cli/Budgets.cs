using Microsoft.Extensions.Configuration;

namespace Ch04.Architecture.Cli;

/// <summary>The latency budget: targets for the user and an allowance for every stage before prefill.</summary>
public sealed record LatencyBudget(int FirstTokenSloMs, int AnswerSloMs, IReadOnlyDictionary<string, int> StageMs)
{
    public int FixedStagesMs => StageMs.Values.Sum();
}

/// <summary>Rates measured on real hardware. The budget is only as honest as these numbers.</summary>
public sealed record MeasuredRates(
    string Source, double PrefillTokensPerSecond, double DecodeTokensPerSecond, int ThreadsPerAnswer,
    double ModelTokensPerO200kToken, double AnswerTokens);

public sealed record Demand(int Employees, double QuestionsPerEmployeePerDay, double PeakHourShare, double TargetUtilization);

/// <summary>Illustrative prices. Replace them with your contract and your hardware costs.</summary>
public sealed record ExamplePrices(double InputPerMillionTokens, double OutputPerMillionTokens, double CoreHour);

/// <summary>Turns budgets and measured rates into the numbers an architecture has to meet.</summary>
public static class BudgetMath
{
    /// <summary>How many prompt tokens the first-token target leaves room for at the measured prefill rate.</summary>
    public static int PromptTokenAllowance(LatencyBudget budget, MeasuredRates rates) =>
        (int)Math.Floor((budget.FirstTokenSloMs - budget.FixedStagesMs) / 1000.0 * rates.PrefillTokensPerSecond);

    public static double FirstTokenSeconds(LatencyBudget budget, MeasuredRates rates, double promptTokensO200k) =>
        budget.FixedStagesMs / 1000.0 + promptTokensO200k * rates.ModelTokensPerO200kToken / rates.PrefillTokensPerSecond;

    public static double AnswerSeconds(LatencyBudget budget, MeasuredRates rates, double promptTokensO200k) =>
        FirstTokenSeconds(budget, rates, promptTokensO200k) + rates.AnswerTokens / rates.DecodeTokensPerSecond;

    /// <summary>Cost of 1,000 answers if a metered model API answered them.</summary>
    public static double MeteredCostPerThousand(ExamplePrices prices, MeasuredRates rates, double promptTokensO200k) =>
        1000 * (promptTokensO200k * rates.ModelTokensPerO200kToken * prices.InputPerMillionTokens
                + rates.AnswerTokens * prices.OutputPerMillionTokens) / 1_000_000;

    /// <summary>Cost of 1,000 answers on your own CPUs: threads held for the length of each answer.</summary>
    public static double CpuCostPerThousand(ExamplePrices prices, MeasuredRates rates, double answerSeconds) =>
        1000 * rates.ThreadsPerAnswer * answerSeconds / 3600 * prices.CoreHour;

    /// <summary>Threads the peak hour keeps busy, before any allowance for queuing or contention.</summary>
    public static double PeakThreads(Demand demand, MeasuredRates rates, double answerSeconds) =>
        demand.Employees * demand.QuestionsPerEmployeePerDay * demand.PeakHourShare * rates.ThreadsPerAnswer * answerSeconds / 3600;

    public static (LatencyBudget Budget, MeasuredRates Rates, Demand Demand, ExamplePrices Prices) Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Budget");
        var stages = section.GetSection("Stages").GetChildren().ToDictionary(s => s.Key, s => int.Parse(s.Value!));
        return (
            new LatencyBudget(section.GetValue<int>("FirstTokenSloMs"), section.GetValue<int>("AnswerSloMs"), stages),
            section.GetSection("Measured").Get<MeasuredRates>()!,
            section.GetSection("Demand").Get<Demand>()!,
            section.GetSection("ExamplePrices").Get<ExamplePrices>()!);
    }
}
