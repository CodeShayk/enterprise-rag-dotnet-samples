using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Contoso.AI;

/// <summary>What the scripted model says or does in reply to one turn.</summary>
public sealed record ScriptReply
{
    /// <summary>The answer text, streamed in small chunks.</summary>
    public string? Text { get; init; }

    /// <summary>Tool calls to make. Calls to tools the request didn't offer are dropped.</summary>
    public IReadOnlyList<ScriptToolCall> ToolCalls { get; init; } = [];

    public static ScriptReply Say(string text) => new() { Text = text };

    public static ScriptReply Call(string toolName, IDictionary<string, object?> arguments, string? text = null) =>
        new() { Text = text, ToolCalls = [new ScriptToolCall(toolName, arguments)] };
}

public sealed record ScriptToolCall(string ToolName, IDictionary<string, object?> Arguments);

/// <summary>
/// One behavior of the scripted model. <see cref="Reply"/> handles a new user turn;
/// <see cref="Summarize"/> handles the turn after tool results come back. Either may return
/// <see langword="null"/> to let the next rule try.
/// </summary>
public sealed record ScriptRule(
    string Name,
    Func<ScriptContext, ScriptReply?>? Reply = null,
    Func<ScriptContext, ScriptReply?>? Summarize = null);

/// <summary>The conversation as the scripted model sees it.</summary>
public sealed class ScriptContext(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
{
    public IReadOnlyList<ChatMessage> Messages { get; } = messages;

    public ChatOptions? Options { get; } = options;

    /// <summary>The text of the most recent user message.</summary>
    public string UserText { get; } =
        messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

    /// <summary>Tool results that arrived since the model's last tool calls.</summary>
    public IReadOnlyList<FunctionResultContent> ToolResults { get; } = TrailingToolResults(messages);

    /// <summary>Whether the request offered a tool with this name.</summary>
    public bool HasTool(string name) =>
        Options?.Tools?.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal)) == true;

    public bool Mentions(params string[] words) =>
        words.Any(w => UserText.Contains(w, StringComparison.OrdinalIgnoreCase));

    public string? Find(string pattern) =>
        Regex.Match(UserText, pattern, RegexOptions.IgnoreCase) is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    private static IReadOnlyList<FunctionResultContent> TrailingToolResults(IReadOnlyList<ChatMessage> messages)
    {
        var results = new List<FunctionResultContent>();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var found = messages[i].Contents.OfType<FunctionResultContent>().ToList();
            if (found.Count == 0)
            {
                break;
            }

            results.InsertRange(0, found);
        }

        return results;
    }
}

public sealed class ScriptedChatClientOptions
{
    /// <summary>The pause between streamed chunks. Use <see cref="TimeSpan.Zero"/> in tests.</summary>
    public TimeSpan ChunkDelay { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <summary>How many words go into each streamed chunk.</summary>
    public int WordsPerChunk { get; init; } = 3;

    /// <summary>The model ID reported on responses.</summary>
    public string ModelId { get; init; } = "contoso-scripted";
}

/// <summary>
/// A deterministic <see cref="IChatClient"/> that follows scripts. It streams like a real model
/// (text deltas and tool calls), so every sample runs offline and every quoted output is
/// reproducible. It is not a language model: it matches words and copies sentences.
/// </summary>
public sealed class ScriptedChatClient(
    IEnumerable<ScriptRule>? rules = null,
    ScriptedChatClientOptions? options = null) : IChatClient
{
    private readonly IReadOnlyList<ScriptRule> _rules = [.. rules ?? ContosoScripts.Default];
    private readonly ScriptedChatClientOptions _options = options ?? new();
    private int _sequence;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken)
            .ConfigureAwait(false);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = new ScriptContext([.. messages], options);
        var reply = Choose(context);

        var turn = Interlocked.Increment(ref _sequence);
        var responseId = $"resp-{turn:D4}";
        var messageId = $"msg-{turn:D4}";

        if (!string.IsNullOrEmpty(reply.Text))
        {
            foreach (var chunk in Chunk(reply.Text))
            {
                await Pause(cancellationToken).ConfigureAwait(false);
                yield return Update(responseId, messageId, new TextContent(chunk));
            }
        }

        var callIndex = 0;
        foreach (var call in reply.ToolCalls.Where(c => context.HasTool(c.ToolName)))
        {
            await Pause(cancellationToken).ConfigureAwait(false);
            var callId = $"call-{turn:D4}-{++callIndex}";
            yield return Update(responseId, messageId, new FunctionCallContent(callId, call.ToolName, call.Arguments));
        }
    }

    private ScriptReply Choose(ScriptContext context)
    {
        if (context.ToolResults.Count > 0)
        {
            foreach (var rule in _rules)
            {
                if (rule.Summarize?.Invoke(context) is { } summary)
                {
                    return summary;
                }
            }

            return ScriptReply.Say(string.Join(" ", context.ToolResults.Select(ResultJson)));
        }

        foreach (var rule in _rules)
        {
            if (rule.Reply?.Invoke(context) is { } reply
                && (reply.ToolCalls.Count == 0 || reply.ToolCalls.Any(c => context.HasTool(c.ToolName))))
            {
                return reply;
            }
        }

        return ContosoScripts.Fallback;
    }

    private ChatResponseUpdate Update(string responseId, string messageId, AIContent content) =>
        new(ChatRole.Assistant, [content])
        {
            ResponseId = responseId,
            MessageId = messageId,
            ModelId = _options.ModelId,
        };

    private IEnumerable<string> Chunk(string text)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i += _options.WordsPerChunk)
        {
            var chunk = string.Join(' ', words.Skip(i).Take(_options.WordsPerChunk));
            yield return i + _options.WordsPerChunk < words.Length ? chunk + " " : chunk;
        }
    }

    private Task Pause(CancellationToken cancellationToken) =>
        _options.ChunkDelay > TimeSpan.Zero ? Task.Delay(_options.ChunkDelay, cancellationToken) : Task.CompletedTask;

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    /// <summary>Renders a tool result as compact JSON, whatever form it arrived in.</summary>
    public static string ResultJson(FunctionResultContent result) => result.Result switch
    {
        null => "null",
        JsonElement element => element.GetRawText(),
        string s => s,
        var other => JsonSerializer.Serialize(other),
    };
}
