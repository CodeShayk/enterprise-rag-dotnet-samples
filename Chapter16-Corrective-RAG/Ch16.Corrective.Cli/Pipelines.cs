using Contoso.Corpus;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;

namespace Ch16.Corrective.Cli;

/// <summary>One answered question: the loop's result and the route the adaptive configuration took.</summary>
public sealed record Outcome(string Id, string Config, string Route, CragResult Result);

/// <summary>
/// The configurations the chapter compares, all built from the same parts. The single pass is Chapter 14's shipped
/// generator; each configuration after it adds one thing: verification and repair, regeneration with feedback, the
/// graded and corrected loop, and the adaptive router in front of it.
/// </summary>
public sealed class Pipelines(IRetriever retriever, OnnxCrossEncoder crossEncoder, OnnxEmbeddingModel bge, OnnxNliModel nli, RecordedChatClient chat, LlmCache cache)
{
    public static readonly string[] Names = ["single", "verify", "verify-regenerate", "crag", "adaptive"];

    /// <summary>The CRAG grader's thresholds, chosen with the graders command (Section 16.3).</summary>
    public const double CorrectFrom = 0.1, IncorrectBelow = 0.005;

    /// <summary>The support another block must give a claim before its citation moves there, chosen with the verify command (Section 16.5).</summary>
    public const double ReciteFrom = 0.5;

    public RecordedChatClient Chat => chat;

    public LlmCache Cache => cache;

    public IRetriever Retriever => retriever;

    public static async Task<Pipelines> CreateAsync(Lab lab)
    {
        var (crossEncoder, bge) = (Lab.CrossEncoder(), Lab.Bge());
        return new Pipelines(await lab.RetrieverAsync(crossEncoder, bge), crossEncoder, bge, Lab.Nli(), Lab.Chat(), Lab.Cache());
    }

    public ContextAssembler Assembler(int maxBlocks = 4) => new(Lab.Calibration, Lab.O200k, new ContextOptions { MaxBlocks = maxBlocks });

    public ExtractiveCompressor Compressor(int tokens = 500) => new(crossEncoder, Lab.O200k, tokens);

    public GroundedAnswerGenerator Generator(IChatClient? live = null) => new(retriever, Assembler(), live ?? chat, Lab.O200k, compressor: Compressor());

    public AnswerVerifier Verifier() => new(new ClaimSupport(nli), 0.05, ReciteFrom);

    public Rewriter Rewriter() => new(chat, cache, new OnnxEmbeddingGenerator(bge));

    public IReadOnlyDictionary<EvidenceGrade, ICorrectionStrategy> Corrections() => new Dictionary<EvidenceGrade, ICorrectionStrategy>
    {
        [EvidenceGrade.Ambiguous] = new WidenEvidence(retriever, Assembler(8), Compressor(800)),
        [EvidenceGrade.Incorrect] = new RewriteAndRetry(retriever, Assembler(), Compressor(), Rewriter(), cache),
    };

    public CorrectiveRag Build(string name, string split) => name switch
    {
        "single" => new(retriever, Assembler(), Compressor(), Generator(), LoopBudget.SinglePass),
        "verify" => new(retriever, Assembler(), Compressor(), Generator(), LoopBudget.SinglePass, verifier: Verifier()),
        "verify-regenerate" => new(retriever, Assembler(), Compressor(), Generator(), new LoopBudget(MaxRetries: 0), verifier: Verifier(), regenerator: new FeedbackRegenerator(chat)),
        "crag" => Crag(split, name),
        _ => throw new ArgumentException($"Unknown configuration '{name}'."),
    };

    /// <summary>The corrective loop; its escalations go to the configuration's own queue.</summary>
    public CorrectiveRag Crag(string split, string config, ICorrectionStrategy? firstRetrieval = null) =>
        new(retriever, Assembler(), Compressor(), Generator(), new LoopBudget(), new CalibratedEvidenceGrader(CorrectFrom, IncorrectBelow), Corrections(),
            Verifier(), new FeedbackRegenerator(chat), new AbstentionPolicy(Lab.Escalations(config, split)), firstRetrieval);

    public async Task<Outcome> AnswerAsync(string name, GoldenQuestion question, string split, CancellationToken cancellationToken = default)
    {
        var asker = People.Find(question.Asker)!;
        if (name != "adaptive")
        {
            return new Outcome(question.Id, name, name, await Build(name, split).RunAsync(question.Question, asker, cancellationToken));
        }

        // Adaptive: the question's form first, then the evidence grade the first retrieval already paid for.
        var before = AdaptiveRouter.Before(question.Question);
        if (before.Route is Route.NoRetrieval)
        {
            var reply = new Answer(AnswerStatus.Answered, "Hello. Ask me about Contoso's manuals, procedures, records and policies.", [], [], 0, "none", new AnswerTimings(0, 0, 0, 0, 0), [], []);
            return new Outcome(question.Id, name, before.Reason, new CragResult(reply, [], null, 0, 0, 0, 0, null));
        }

        var loop = before.Route is Route.Iterative
            ? Crag(split, name, new DecomposeAndRetrieve(retriever, Assembler(6), Compressor(800), Rewriter(), cache))
            : Crag(split, name);
        var run = new CragRun(question.Question, asker, new BudgetMeter(loop.Budget));
        await loop.RetrieveAsync(run, cancellationToken);
        var next = await loop.GradeAsync(run, cancellationToken);
        var after = AdaptiveRouter.After(before, run.Verdict!);
        var pipeline = after.Route is Route.SinglePass
            ? new CorrectiveRag(retriever, Assembler(), Compressor(), Generator(), LoopBudget.SinglePass, verifier: Verifier(), abstention: new AbstentionPolicy(Lab.Escalations(name, split)))
            : loop;
        return new Outcome(question.Id, name, after.Reason, await pipeline.ContinueAsync(run, next, cancellationToken));
    }
}
