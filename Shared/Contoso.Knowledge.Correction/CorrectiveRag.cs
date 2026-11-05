using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;

namespace Contoso.Knowledge.Correction;

/// <summary>The states of the corrective loop. Each step returns the next one; the transitions are the policy.</summary>
public enum CragState { Retrieve, Grade, Correct, Generate, Verify, Regenerate, Abstain, Done }

/// <summary>One step of a request: what it decided and the milliseconds it took (a model call's as recorded).</summary>
public sealed record CragStep(CragState State, string Detail, double Milliseconds);

/// <summary>Everything one request knows as it moves through the loop.</summary>
public sealed class CragRun(string question, Person asker, BudgetMeter meter)
{
    public string Question => question;

    public Person Asker => asker;

    public BudgetMeter Meter => meter;

    public AssembledContext Context { get; set; } = new([], 0, 0, []);

    public EvidenceVerdict? Verdict { get; set; }

    /// <summary>The best-graded evidence so far: a correction that makes things worse doesn't replace it.</summary>
    public (AssembledContext Context, EvidenceVerdict Verdict)? Best { get; set; }

    public HashSet<string> Tried { get; } = [];

    public Answer? Answer { get; set; }

    public Verification? Verification { get; set; }

    public bool Regenerated { get; set; }

    public AbstentionReason? Abstention { get; set; }

    public List<CragStep> Steps { get; } = [];

    public void Step(CragState state, string detail, double milliseconds) => Steps.Add(new CragStep(state, detail, milliseconds));
}

/// <summary>The outcome of one request: the answer, the steps that led to it and what the request spent.</summary>
public sealed record CragResult(Answer Answer, IReadOnlyList<CragStep> Steps, EvidenceGrade? Grade, int Retries, int ModelCalls, long PromptTokens, double Seconds, string? Exhausted);

/// <summary>
/// Corrective RAG as an explicit state machine around Chapter 14's generator. Retrieve, grade the evidence, correct
/// it while the grade and the budget allow, generate, verify the claims, regenerate once with feedback if some claim
/// nothing supports, repair the citations, or abstain. Leave out the grader, the corrections, the verifier or the
/// regenerator and the same machine runs the simpler configurations the chapter compares.
/// </summary>
public sealed class CorrectiveRag(
    IRetriever retriever, ContextAssembler assembler, ExtractiveCompressor compressor, GroundedAnswerGenerator generator,
    LoopBudget budget, IEvidenceGrader? grader = null, IReadOnlyDictionary<EvidenceGrade, ICorrectionStrategy>? corrections = null,
    AnswerVerifier? verifier = null, FeedbackRegenerator? regenerator = null, AbstentionPolicy? abstention = null,
    ICorrectionStrategy? firstRetrieval = null)
{
    private readonly AbstentionPolicy _abstention = abstention ?? new AbstentionPolicy();

    public LoopBudget Budget => budget;

    public Task<CragResult> RunAsync(string question, Person asker, CancellationToken cancellationToken = default) =>
        ContinueAsync(new CragRun(question, asker, new BudgetMeter(budget)), CragState.Retrieve, cancellationToken);

    public async Task<CragResult> ContinueAsync(CragRun run, CragState state, CancellationToken cancellationToken = default)
    {
        while (state != CragState.Done)
        {
            state = state switch
            {
                CragState.Retrieve => await RetrieveAsync(run, cancellationToken),
                CragState.Grade => await GradeAsync(run, cancellationToken),
                CragState.Correct => await CorrectAsync(run, cancellationToken),
                CragState.Generate => await GenerateAsync(run, cancellationToken),
                CragState.Verify => Verify(run),
                CragState.Regenerate => await RegenerateAsync(run, cancellationToken),
                CragState.Abstain => await AbstainAsync(run, cancellationToken),
                _ => throw new InvalidOperationException($"No step for {state}."),
            };
        }

        var meter = run.Meter;
        return new CragResult(run.Answer!, run.Steps, run.Verdict?.Grade, meter.Retries, meter.ModelCalls, meter.PromptTokens, meter.Seconds, meter.Exhausted);
    }

    public async Task<CragState> RetrieveAsync(CragRun run, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        if (firstRetrieval is not null)
        {
            var corrected = await firstRetrieval.CorrectAsync(run.Question, run.Asker, cancellationToken);
            run.Context = corrected.Context;
            run.Step(CragState.Retrieve, $"{firstRetrieval.Name}: {corrected.Query}", corrected.Milliseconds);
        }
        else
        {
            var result = await retriever.RetrieveAsync(new RetrievalRequest(run.Question, run.Asker, generator.Options.Top), cancellationToken);
            var context = assembler.Assemble(result.Hits);
            run.Context = context.IsEmpty ? context : compressor.Compress(run.Question, context);
            run.Step(CragState.Retrieve, $"{run.Context.Blocks.Count} blocks, {run.Context.Tokens} tokens", clock.Elapsed.TotalMilliseconds);
        }

        return grader is not null ? CragState.Grade : run.Context.IsEmpty ? CragState.Abstain : CragState.Generate;
    }

    /// <summary>CRAG's decision: generate from evidence that answers, correct what's doubtful while retries remain.</summary>
    public async Task<CragState> GradeAsync(CragRun run, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var verdict = await grader!.GradeAsync(run.Question, run.Context, cancellationToken);
        run.Verdict = verdict;
        if (run.Best is not { } best || Rank(verdict) >= Rank(best.Verdict)) run.Best = (run.Context, verdict);
        run.Step(CragState.Grade, $"{verdict.Grade}: {verdict.Reason}", clock.Elapsed.TotalMilliseconds);

        if (verdict.Grade is EvidenceGrade.Correct) return CragState.Generate;
        if (Next(run) is not null && run.Meter.CanRetry) return CragState.Correct;

        // Out of corrections: answer from the best evidence if any of it was relevant, otherwise there's nothing to say.
        (run.Context, run.Verdict) = run.Best!.Value;
        if (run.Verdict.Grade is EvidenceGrade.Incorrect || run.Context.IsEmpty)
        {
            run.Abstention = AbstentionReason.NothingRelevant;
            return CragState.Abstain;
        }

        return CragState.Generate;
    }

    public async Task<CragState> CorrectAsync(CragRun run, CancellationToken cancellationToken = default)
    {
        var strategy = Next(run)!;
        run.Tried.Add(strategy.Name);
        run.Meter.Retry();
        var result = await strategy.CorrectAsync(run.Question, run.Asker, cancellationToken);
        run.Context = result.Context;
        run.Step(CragState.Correct, $"{strategy.Name}: {result.Query}", result.Milliseconds);
        return CragState.Grade;
    }

    public async Task<CragState> GenerateAsync(CragRun run, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        Answer? answer = null;
        try
        {
            await foreach (var e in generator.StreamAsync(run.Question, run.Asker, evidence: run.Context.Blocks, cancellationToken: cancellationToken))
            {
                if (e is AnswerReady ready) answer = ready.Answer;
            }
        }
        catch (ModelUnavailableException e)
        {
            // Graceful degradation: the sources the user can open, without an answer the model couldn't write.
            var sources = string.Join("; ", run.Context.Blocks.Select(b => b.Title).Distinct());
            run.Answer = new Answer(AnswerStatus.NotFound, $"I can't write an answer right now. These documents look relevant: {sources}.", [], run.Context.Blocks, 0,
                GroundingPrompt.Version, new AnswerTimings(0, 0, 0, 0, 0), ["model unavailable"], [e.Message]);
            run.Step(CragState.Generate, "model unavailable: evidence only", clock.Elapsed.TotalMilliseconds);
            return CragState.Done;
        }

        run.Answer = answer!;
        run.Meter.Spend(answer!.Timings.PromptTokens, answer.Timings.TotalMs / 1000, clock.Elapsed.TotalSeconds);
        run.Step(CragState.Generate, $"{answer.Status}, {answer.Claims.Count} claims", answer.Timings.TotalMs);
        return verifier is not null && answer.Claims.Count > 0 ? CragState.Verify : CragState.Done;
    }

    /// <summary>Verify, then regenerate once with feedback, or repair and finish, or abstain when nothing survived.</summary>
    public CragState Verify(CragRun run)
    {
        var clock = Stopwatch.StartNew();
        var verification = verifier!.Verify(run.Answer!);
        run.Verification = verification;
        run.Step(CragState.Verify, $"{verification.Supported} supported, {verification.Repairable} re-citable, {verification.Unsupported} unsupported", clock.Elapsed.TotalMilliseconds);

        if (verification.Unsupported > 0 && regenerator is not null && !run.Regenerated && run.Meter.CanCallModel(run.Answer!.Timings.PromptTokens)) return CragState.Regenerate;

        run.Answer = verifier.Repair(run.Answer!, verification);
        if (run.Answer.Status is AnswerStatus.NotFound && verification.Checks.Count > 0)
        {
            run.Abstention = AbstentionReason.NothingSupported;
            return CragState.Abstain;
        }

        return CragState.Done;
    }

    public async Task<CragState> RegenerateAsync(CragRun run, CancellationToken cancellationToken = default)
    {
        (Answer Answer, double Wall) regenerated;
        try
        {
            regenerated = await regenerator!.RegenerateAsync(run.Question, run.Asker, run.Answer!, run.Verification!, cancellationToken);
        }
        catch (ModelUnavailableException)
        {
            // Keep the first answer and repair it: a verified answer with less in it beats no answer.
            run.Regenerated = true;
            run.Step(CragState.Regenerate, "model unavailable: repairing the first answer", 0);
            return CragState.Verify;
        }

        var (answer, wall) = regenerated;
        run.Meter.Spend(answer.Timings.PromptTokens, answer.Timings.TotalMs / 1000, wall);
        (run.Answer, run.Regenerated) = (answer, true);
        run.Step(CragState.Regenerate, $"{answer.Status}, {answer.Claims.Count} claims", answer.Timings.TotalMs);
        return answer.Claims.Count > 0 ? CragState.Verify : CragState.Done;
    }

    public async Task<CragState> AbstainAsync(CragRun run, CancellationToken cancellationToken = default)
    {
        var reason = run.Abstention ?? AbstentionReason.NothingRelevant;
        var timings = run.Answer?.Timings ?? new AnswerTimings(0, 0, 0, 0, 0);
        run.Answer = await _abstention.AbstainAsync(run.Question, run.Asker, reason, run.Context.Blocks, timings, cancellationToken);
        run.Step(CragState.Abstain, reason.ToString(), 0);
        return CragState.Done;
    }

    /// <summary>The correction for the current grade, or the other one if that was tried; null when none is left.</summary>
    private ICorrectionStrategy? Next(CragRun run)
    {
        if (corrections is null || run.Verdict is null) return null;
        var preferred = corrections.GetValueOrDefault(run.Verdict.Grade);
        return preferred is not null && !run.Tried.Contains(preferred.Name) ? preferred
            : corrections.Values.FirstOrDefault(c => !run.Tried.Contains(c.Name));
    }

    /// <summary>The grade first, then the score. A tie goes to the later evidence: widening keeps the best parent and adds to it.</summary>
    private static double Rank(EvidenceVerdict verdict) => verdict.Grade switch { EvidenceGrade.Correct => 2, EvidenceGrade.Ambiguous => 1, _ => 0 } + verdict.Score * 0.999;
}
