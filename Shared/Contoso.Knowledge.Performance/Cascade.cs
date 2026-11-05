using Contoso.Corpus;
using Contoso.Knowledge.Generation;

namespace Contoso.Knowledge.Performance;

/// <summary>A cascade's answer, the small model's first attempt and whether the large model had to answer.</summary>
public sealed record CascadeResult(Answer Answer, Answer First, bool Escalated);

/// <summary>
/// Chapter 14's generator with a small model first. Its answer stands when Chapter 14's validation finds a source and a
/// quote for every claim; anything else (an invalid citation, an abstention, a reply that isn't the schema's JSON)
/// goes to the large model with the same evidence, so retrieval runs once.
/// </summary>
public sealed class ModelCascade(GroundedAnswerGenerator small, GroundedAnswerGenerator large)
{
    public static bool Accept(Answer answer) =>
        answer.Status is AnswerStatus.Answered or AnswerStatus.Partial && answer.Confidence >= 1 && answer.Problems.Count == 0;

    public async Task<CascadeResult> AnswerAsync(string question, Person asker, CancellationToken cancellationToken = default)
    {
        var first = await LastAsync(small.StreamAsync(question, asker, cancellationToken: cancellationToken));
        if (first.Evidence.Count == 0 || Accept(first)) return new CascadeResult(first, first, Escalated: false);

        var second = await LastAsync(large.StreamAsync(question, asker, evidence: first.Evidence, cancellationToken: cancellationToken));
        return new CascadeResult(second, first, Escalated: true);
    }

    private static async Task<Answer> LastAsync(IAsyncEnumerable<AnswerEvent> events)
    {
        Answer? answer = null;
        await foreach (var e in events)
        {
            if (e is AnswerReady ready) answer = ready.Answer;
        }

        return answer ?? throw new InvalidOperationException("The generator ended without an answer.");
    }
}
