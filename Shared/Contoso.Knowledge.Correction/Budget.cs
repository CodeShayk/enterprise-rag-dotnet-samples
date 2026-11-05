using System.Diagnostics;

namespace Contoso.Knowledge.Correction;

/// <summary>
/// The limits of one request. A loop that grades and corrects can always find one more thing to try; the budget says
/// when it stops: retries of retrieval, calls to the model, prompt tokens and seconds.
/// </summary>
public sealed record LoopBudget(int MaxRetries = 2, int MaxModelCalls = 2, long MaxPromptTokens = 4_000, double DeadlineSeconds = 45)
{
    /// <summary>Chapter 14's single pass: one retrieval, one call.</summary>
    public static LoopBudget SinglePass { get; } = new(0, 1);
}

/// <summary>
/// What one request has spent, checked before every step that costs something. Time is the request's own: the CPU
/// time of retrieval and grading as measured, plus each model call's time as recorded, so a replayed call counts
/// against the deadline exactly as it did when it ran.
/// </summary>
public sealed class BudgetMeter(LoopBudget budget)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _modelWallSeconds;

    public LoopBudget Budget => budget;

    public int Retries { get; private set; }

    public int ModelCalls { get; private set; }

    public long PromptTokens { get; private set; }

    public double Seconds => _clock.Elapsed.TotalSeconds - _modelWallSeconds + ModelSeconds;

    public double ModelSeconds { get; private set; }

    public string? Exhausted { get; private set; }

    public bool CanRetry => Allow(Retries < budget.MaxRetries, "retries") && WithinDeadline;

    public bool WithinDeadline => Allow(Seconds < budget.DeadlineSeconds, "deadline");

    public bool CanCallModel(long promptTokens) =>
        Allow(ModelCalls < budget.MaxModelCalls, "model calls") && Allow(PromptTokens + promptTokens <= budget.MaxPromptTokens, "prompt tokens") && WithinDeadline;

    public void Retry() => Retries++;

    /// <summary>Counts a model call: its prompt tokens, the seconds it took as recorded and the wall time it really took.</summary>
    public void Spend(long promptTokens, double recordedSeconds, double wallSeconds)
    {
        ModelCalls++;
        PromptTokens += promptTokens;
        ModelSeconds += recordedSeconds;
        _modelWallSeconds += wallSeconds;
    }

    private bool Allow(bool ok, string limit)
    {
        if (!ok) Exhausted ??= limit;
        return ok;
    }
}
