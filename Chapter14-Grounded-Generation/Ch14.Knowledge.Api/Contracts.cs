using Contoso.Knowledge.Generation;

namespace Ch14.Knowledge.Api;

/// <summary>Who is asking, what, and in which conversation. Chapter 22 replaces the user field with the signed-in identity.</summary>
public sealed record AskRequest(string User, string Question, string? ConversationId = null);

/// <summary>One source as the source panel shows it, sent before the answer is written.</summary>
public sealed record SourceView(string Id, string DocumentId, string Title, string? Section, double Probability, string Reference, string Text)
{
    public static SourceView From(EvidenceBlock block) =>
        new(block.Id, block.DocumentId, block.Title, block.Section, Math.Round(block.Probability, 3), block.Reference, block.Text);
}

public sealed record SourcesView(IReadOnlyList<SourceView> Sources, IReadOnlyList<string> Degraded);

public sealed record Delta(string Text);

public sealed record ClaimView(string Text, string Source, string Quote, bool Verified, string? DocumentId, int Start, int End);

/// <summary>The typed answer as the client receives it.</summary>
public sealed record AnswerView(
    string Status, string Text, IReadOnlyList<ClaimView> Claims, IReadOnlyList<SourceView> Sources, double Confidence,
    string PromptVersion, IReadOnlyList<string> Problems, IReadOnlyList<string> Degraded, double FirstTokenMs, double TotalMs)
{
    public static AnswerView From(Answer answer) => new(
        answer.Status switch { AnswerStatus.Answered => "answered", AnswerStatus.Partial => "partial", AnswerStatus.NotFound => "not_found", _ => "clarify" },
        answer.Text,
        [.. answer.Claims.Select(c => new ClaimView(c.Text, c.Source, c.Quote, c.Valid, c.DocumentId, c.Start, c.End))],
        [.. answer.Evidence.Select(SourceView.From)],
        Math.Round(answer.Confidence, 2),
        answer.PromptVersion,
        answer.Problems,
        answer.Degraded,
        Math.Round(answer.Timings.FirstTokenMs),
        Math.Round(answer.Timings.TotalMs));
}

/// <summary>A rating of one answer, kept with what produced it so a bad answer can be traced (Chapters 15 and 20).</summary>
public sealed record FeedbackRequest(
    string User, string Question, string Rating, string PromptVersion, IReadOnlyList<string> Sources, string? Comment = null, string? ConversationId = null);

/// <summary>Ratings kept in memory and written to the log; Chapter 20's <c>rag.feedback</c> counter, tagged by tenant, is where production ratings go.</summary>
public sealed class FeedbackLog(ILogger<FeedbackLog> logger)
{
    private readonly List<(DateTimeOffset At, FeedbackRequest Feedback)> _entries = [];

    public void Add(FeedbackRequest feedback)
    {
        lock (_entries) _entries.Add((DateTimeOffset.UtcNow, feedback));
        logger.LogInformation("Feedback {Rating} from {User} on prompt {PromptVersion} with sources {Sources}: {Comment}",
            feedback.Rating, feedback.User, feedback.PromptVersion, string.Join(", ", feedback.Sources), feedback.Comment);
    }

    public IReadOnlyList<(DateTimeOffset At, FeedbackRequest Feedback)> Entries
    {
        get { lock (_entries) return [.. _entries]; }
    }
}
