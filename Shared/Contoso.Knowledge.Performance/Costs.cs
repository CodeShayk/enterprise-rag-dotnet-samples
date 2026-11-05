using System.Diagnostics;

namespace Contoso.Knowledge.Performance;

/// <summary>Chapter 4's example prices: an API model's tokens, and a core-hour of a CPU you run yourself.</summary>
public sealed record Prices(decimal InputPerMillion = 1.00m, decimal OutputPerMillion = 4.00m, decimal PerCoreHour = 0.05m);

/// <summary>
/// What one answer used: CPU-seconds in the application (retrieval, re-ranking, compression, validation) and in the model
/// server, the model's tokens and the time the asker waited.
/// </summary>
public sealed record AnswerUsage(double AppCpuSeconds, double ModelCpuSeconds, long InputTokens, long OutputTokens, double WallSeconds);

/// <summary>Cost per answer from what it used, under two ways of running the model.</summary>
public static class CostModel
{
    /// <summary>The model on your own CPU: every CPU-second the answer took, at the core-hour price.</summary>
    public static decimal Local(AnswerUsage usage, Prices prices) =>
        (decimal)(usage.AppCpuSeconds + usage.ModelCpuSeconds) / 3600m * prices.PerCoreHour;

    /// <summary>The model behind a metered API: its tokens, plus the application's own CPU for everything else.</summary>
    public static decimal Api(AnswerUsage usage, Prices prices) =>
        (usage.InputTokens * prices.InputPerMillion + usage.OutputTokens * prices.OutputPerMillion) / 1_000_000m +
        (decimal)usage.AppCpuSeconds / 3600m * prices.PerCoreHour;

    /// <summary>
    /// A node you pay for by the hour costs its cores whether it's busy or not: its cost per answer at the answers per
    /// hour it actually serves.
    /// </summary>
    public static decimal PerAnswerOnNode(int cores, double answersPerHour, Prices prices) =>
        answersPerHour <= 0 ? 0 : cores * prices.PerCoreHour / (decimal)answersPerHour;
}

/// <summary>
/// CPU time of this process and of the model server's processes, read before and after a piece of work. Valid when the
/// work runs alone: with requests in parallel, each one's share can't be told apart.
/// </summary>
public sealed class CpuMeter(params string[] modelProcesses)
{
    private readonly string[] _names = modelProcesses.Length > 0 ? modelProcesses : ["ollama", "llama-server"];

    public (TimeSpan App, TimeSpan Model) Read()
    {
        var app = Process.GetCurrentProcess().TotalProcessorTime;
        var model = TimeSpan.Zero;
        foreach (var name in _names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        model += process.TotalProcessorTime;
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between listing and reading.
                    }
                }
            }
        }

        return (app, model);
    }

    /// <summary>Runs the work and returns its result with the CPU-seconds it took on each side.</summary>
    public async Task<(T Result, double AppSeconds, double ModelSeconds)> MeasureAsync<T>(Func<Task<T>> work)
    {
        var before = Read();
        var result = await work();
        var after = Read();
        return (result, (after.App - before.App).TotalSeconds, Math.Max(0, (after.Model - before.Model).TotalSeconds));
    }
}
