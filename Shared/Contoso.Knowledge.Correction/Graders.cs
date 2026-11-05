using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Contoso.Knowledge.Generation;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Correction;

/// <summary>CRAG's three verdicts on retrieved evidence, each with its own action.</summary>
public enum EvidenceGrade
{
    /// <summary>The evidence answers the question: generate from it.</summary>
    Correct,

    /// <summary>The evidence is about the question but may not answer it: refine it, or widen the search.</summary>
    Ambiguous,

    /// <summary>Nothing retrieved answers the question: search again differently, or abstain.</summary>
    Incorrect,
}

/// <summary>A grader's verdict on one question's evidence, with the score behind it.</summary>
public sealed record EvidenceVerdict(EvidenceGrade Grade, double Score, string Reason);

public interface IEvidenceGrader
{
    string Name { get; }

    Task<EvidenceVerdict> GradeAsync(string question, AssembledContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// CRAG's retrieval evaluator on the CPU: Chapter 12's calibrated probability that the best parent is relevant, read
/// against two thresholds. It costs nothing the re-ranker didn't already spend.
/// </summary>
public sealed class CalibratedEvidenceGrader(double correctFrom = 0.5, double incorrectBelow = 0.005) : IEvidenceGrader
{
    public string Name => $"calibrated P(relevant), correct from {correctFrom}, incorrect below {incorrectBelow}";

    public Task<EvidenceVerdict> GradeAsync(string question, AssembledContext context, CancellationToken cancellationToken = default)
    {
        var p = context.BestProbability;
        var grade = p >= correctFrom ? EvidenceGrade.Correct : p < incorrectBelow ? EvidenceGrade.Incorrect : EvidenceGrade.Ambiguous;
        return Task.FromResult(new EvidenceVerdict(grade, p, $"best parent P(relevant) {p:0.000}"));
    }
}

/// <summary>Whether evidence answers a question, as an LLM grader says it; the enum keeps the model to three values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Sufficiency>))]
public enum Sufficiency
{
    [JsonStringEnumMemberName("answers")] Answers,
    [JsonStringEnumMemberName("partly")] Partly,
    [JsonStringEnumMemberName("does_not")] DoesNot,
}

public sealed record SufficiencyVerdict(
    [property: Description("answers, partly or does_not")] Sufficiency Verdict,
    [property: Description("One sentence that says why")] string Reason);

/// <summary>
/// An LLM grader: the model reads the question and the numbered evidence and says whether the evidence states the
/// answer, not merely whether it's about the same subject. Relevance isn't sufficiency: a torque table is relevant
/// to an M20 bolt it doesn't list.
/// </summary>
public sealed class LlmEvidenceGrader(IChatClient chat) : IEvidenceGrader
{
    public const string Instructions = """
        You check whether evidence from a company's documents answers a question. Read the question and the evidence, then choose:
        answers: the evidence states everything the question asks.
        partly: the evidence states some of what the question asks.
        does_not: the evidence doesn't state the answer, even if it's about the same subject.
        """;

    public string Name => "llama3.2 sufficiency";

    public async Task<EvidenceVerdict> GradeAsync(string question, AssembledContext context, CancellationToken cancellationToken = default)
    {
        if (context.IsEmpty) return new EvidenceVerdict(EvidenceGrade.Incorrect, 0, "no evidence");
        var evidence = new StringBuilder();
        foreach (var block in context.Blocks) evidence.Append($"[{block.Id}] {block.Section}\n{block.Text}\n\n");
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions.ReplaceLineEndings("\n")),
            new(ChatRole.User, $"<question>{question}</question>\n<evidence>\n{evidence.ToString().Trim()}\n</evidence>"),
        ];
        var response = await chat.GetResponseAsync<SufficiencyVerdict>(messages, new ChatOptions { Temperature = 0, MaxOutputTokens = 150 }, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var verdict)) return new EvidenceVerdict(EvidenceGrade.Ambiguous, 0.5, "the verdict couldn't be read");
        return verdict.Verdict switch
        {
            Sufficiency.Answers => new EvidenceVerdict(EvidenceGrade.Correct, 1, verdict.Reason),
            Sufficiency.Partly => new EvidenceVerdict(EvidenceGrade.Ambiguous, 0.5, verdict.Reason),
            _ => new EvidenceVerdict(EvidenceGrade.Incorrect, 0, verdict.Reason),
        };
    }
}
