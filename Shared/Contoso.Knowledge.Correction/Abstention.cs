using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;

namespace Contoso.Knowledge.Correction;

/// <summary>A question handed to people because the assistant couldn't answer it from evidence it found.</summary>
public sealed record Escalation(string Question, string Asker, string Reason, IReadOnlyList<string> Sources, DateTimeOffset At);

public interface IEscalationQueue
{
    ValueTask EnqueueAsync(Escalation escalation, CancellationToken cancellationToken = default);
}

/// <summary>An escalation queue in a JSON Lines file; a real one is a ticket system the knowledge owners work from.</summary>
public sealed class JsonlEscalationQueue(string path) : IEscalationQueue
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async ValueTask EnqueueAsync(Escalation escalation, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(escalation, JsonSerializerOptions.Web) + "\n", cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Why the loop ended without an answer.</summary>
public enum AbstentionReason
{
    /// <summary>Nothing the asker may read is relevant: missing content, or a question outside the documents.</summary>
    NothingRelevant,

    /// <summary>Relevant evidence was found, but no claim the model wrote survived verification.</summary>
    NothingSupported,
}

/// <summary>
/// When the assistant says it can't answer, and what happens next. The words never reveal whether an answer exists
/// where the asker can't see it (Chapter 14). Nothing relevant is a content gap, logged for the document owners; relevant
/// evidence that produced no supported answer goes to people, with the sources it found.
/// </summary>
public sealed class AbstentionPolicy(IEscalationQueue? queue = null)
{
    public const string Escalated = " I've passed your question to the knowledge team with the documents I found.";

    public async Task<Answer> AbstainAsync(string question, Person asker, AbstentionReason reason, IReadOnlyList<EvidenceBlock> evidence, AnswerTimings timings, CancellationToken cancellationToken = default)
    {
        var escalate = reason is AbstentionReason.NothingSupported && queue is not null;
        if (escalate)
        {
            await queue!.EnqueueAsync(new Escalation(question, asker.Id, reason.ToString(), [.. evidence.Select(b => b.Reference).Distinct()], DateTimeOffset.UtcNow), cancellationToken);
        }

        var text = GroundedAnswerGenerator.NotFoundText + (escalate ? Escalated : "");
        return new Answer(AnswerStatus.NotFound, text, [], evidence, 0, GroundingPrompt.Version, timings, [], [$"abstained: {reason}"]);
    }
}
