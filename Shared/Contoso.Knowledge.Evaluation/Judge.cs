using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Evaluation;

/// <summary>A judge's grade of one answer against the reference facts, returned through a JSON schema.</summary>
public sealed record JudgeVerdict(
    [property: Description("1 to 5, by the rubric")] int Score,
    [property: Description("One sentence that says why")] string Reason)
{
    /// <summary>Scores 4 and 5 pass: every reference fact is there and nothing contradicts them.</summary>
    public bool Correct => Score >= 4;
}

/// <summary>
/// Which of two answers a judge prefers. An enum makes the schema list the three values, and Ollama's grammar keeps
/// the model to them; a description alone let llama3.2 copy an answer's text into the field.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PairChoice>))]
public enum PairChoice
{
    [JsonStringEnumMemberName("answer_a")] A,
    [JsonStringEnumMemberName("answer_b")] B,
    [JsonStringEnumMemberName("tie")] Tie,
}

/// <summary>A judge's choice between two answers to the same question.</summary>
public sealed record PairVerdict(
    [property: Description("answer_a, answer_b or tie")] PairChoice Better,
    [property: Description("One sentence that says why")] string Reason);

/// <summary>
/// A local model as a judge. The rubric anchors every score to what an expert's reference facts say, the verdict comes
/// back through a JSON schema, and the rubric is versioned like a prompt (Chapter 14). Calibrate it against people
/// before trusting a number it produces (Section 15.6).
/// </summary>
public sealed class LocalJudge(IChatClient chat)
{
    public const string Rubric = """
        You grade answers from a company's knowledge assistant. Compare the answer with the reference facts, which experts took from the company's documents, and grade it on this scale:
        5: the answer states every reference fact correctly and nothing that contradicts them.
        4: the answer states every reference fact correctly but adds a detail the reference facts don't mention.
        3: the answer states some reference facts correctly, misses others, and contradicts none.
        2: the answer contradicts a reference fact, or says the information isn't available although the reference facts give it.
        1: the answer is wrong or doesn't address the question.
        Judge the content, not the length or the style.
        """;

    public const string PairRubric = """
        You compare two answers from a company's knowledge assistant with reference facts that experts took from the company's documents.
        Choose the answer that states the reference facts more correctly and completely without contradicting them: answer_a, answer_b, or tie when they are equally good. Judge the content, not the length or the style.
        """;

    public static string Version { get; } = "judge-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((Rubric + PairRubric).ReplaceLineEndings("\n"))))[..8];

    public async Task<JudgeVerdict?> GradeAsync(string question, IReadOnlyList<string> references, string answer, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Rubric.ReplaceLineEndings("\n")),
            new(ChatRole.User, $"<question>{question}</question>\n<reference>\n{string.Join('\n', references)}\n</reference>\n<answer>{answer}</answer>"),
        ];
        var response = await chat.GetResponseAsync<JudgeVerdict>(messages, new ChatOptions { Temperature = 0, MaxOutputTokens = 200 }, cancellationToken: cancellationToken);
        return response.TryGetResult(out var verdict) && verdict.Score is >= 1 and <= 5 ? verdict : null;
    }

    public async Task<PairVerdict?> CompareAsync(string question, IReadOnlyList<string> references, string answerA, string answerB, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, PairRubric.ReplaceLineEndings("\n")),
            new(ChatRole.User, $"<question>{question}</question>\n<reference>\n{string.Join('\n', references)}\n</reference>\n<answer_a>{answerA}</answer_a>\n<answer_b>{answerB}</answer_b>"),
        ];
        var response = await chat.GetResponseAsync<PairVerdict>(messages, new ChatOptions { Temperature = 0, MaxOutputTokens = 200 }, cancellationToken: cancellationToken);
        return response.TryGetResult(out var verdict) ? verdict : null;
    }
}
