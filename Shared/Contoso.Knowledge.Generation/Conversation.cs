using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Generation;

/// <summary>One question and the answer it got.</summary>
public sealed record Turn(string Question, Answer Answer);

/// <summary>
/// The part of a conversation a prompt carries: the most recent turns in full while they fit the history budget, and
/// only the questions of the turns before them. Answers are where the tokens are, and an old answer's evidence is gone.
/// </summary>
public sealed record HistoryWindow(IReadOnlyList<string> Earlier, IReadOnlyList<Turn> Recent)
{
    public bool IsEmpty => Earlier.Count == 0 && Recent.Count == 0;

    public static HistoryWindow Of(IReadOnlyList<Turn> turns, Tokenizer tokenizer, int maxTokens = 400)
    {
        var recent = new List<Turn>();
        var tokens = 0;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            var cost = tokenizer.CountTokens(turns[i].Question) + tokenizer.CountTokens(turns[i].Answer.Text);
            if (recent.Count > 0 && tokens + cost > maxTokens) break;
            recent.Insert(0, turns[i]);
            tokens += cost;
        }

        return new HistoryWindow([.. turns.Take(turns.Count - recent.Count).Select(t => t.Question)], recent);
    }
}

/// <summary>Where a conversation's turns live between requests.</summary>
public interface IConversationStore
{
    Task<IReadOnlyList<Turn>> GetAsync(string conversationId, CancellationToken cancellationToken = default);

    Task AppendAsync(string conversationId, Turn turn, CancellationToken cancellationToken = default);
}

/// <summary>
/// Conversations in the process's memory, the newest turns kept. One instance of the API only: a restart forgets them,
/// and a second instance never sees them. Chapter 21 moves them into a distributed cache.
/// </summary>
public sealed class InMemoryConversationStore(int maxTurns = 20) : IConversationStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<Turn>> _conversations = new();

    public Task<IReadOnlyList<Turn>> GetAsync(string conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_conversations.GetValueOrDefault(conversationId) ?? []);

    public Task AppendAsync(string conversationId, Turn turn, CancellationToken cancellationToken = default)
    {
        _conversations.AddOrUpdate(conversationId, [turn], (_, turns) => [.. turns.TakeLast(maxTurns - 1), turn]);
        return Task.CompletedTask;
    }
}

/// <summary>Requests about the previous answer rather than about Contoso's documents.</summary>
public static partial class FollowUps
{
    /// <summary>"Make that shorter", "as a list": rework the last answer from its evidence, without retrieving again.</summary>
    public static bool IsRework(string question) => Rework().IsMatch(question.Trim());

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:make|keep|write)\s+(?:it|that|this)\s+(?:shorter|simpler|briefer|as\s+a\s+list)|(?:shorten|summari[sz]e|simplify)\s+(?:it|that|this)|(?:shorter|simpler|as\s+a\s+list)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex Rework();
}
