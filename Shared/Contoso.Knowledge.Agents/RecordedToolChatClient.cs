using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contoso.Knowledge.Generation;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Agents;

/// <summary>
/// Records each model response of a tool-calling loop, function calls included, and replays it for the next request
/// that is the same: the instructions, the tools as the model sees them and every message, with function calls by
/// name and arguments and their results by text. Chapter 14's recorder keeps text only, which can't replay a call.
/// </summary>
public sealed class RecordedToolChatClient : DelegatingChatClient
{
    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RecordedToolChatClient(IChatClient inner, string path) : base(inner)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path).Where(l => l.Length > 0))
        {
            var entry = JsonSerializer.Deserialize<Entry>(line)!;
            _entries[entry.Key] = entry;
        }
    }

    public int Replayed { get; private set; }

    public int Recorded { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var key = Key(list, options);
        if (!_entries.TryGetValue(key, out var entry))
        {
            var clock = Stopwatch.StartNew();
            var live = await base.GetResponseAsync(list, options, cancellationToken);
            entry = new Entry(key, JsonSerializer.Serialize(live.Messages, AIJsonUtilities.DefaultOptions), clock.Elapsed.TotalMilliseconds,
                live.Usage?.InputTokenCount ?? 0, live.Usage?.OutputTokenCount ?? 0, DateTimeOffset.UtcNow);
            await _lock.WaitAsync(cancellationToken);
            try
            {
                _entries[key] = entry;
                Recorded++;
                await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(entry) + "\n", cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }
        else
        {
            Replayed++;
        }

        // Live or replayed, the response reports the time the model took when it was recorded.
        return new ChatResponse(JsonSerializer.Deserialize<List<ChatMessage>>(entry.Messages, AIJsonUtilities.DefaultOptions)!)
        {
            Usage = new UsageDetails { InputTokenCount = entry.InputTokens, OutputTokenCount = entry.OutputTokens },
            AdditionalProperties = new() { [RecordedChatClient.FirstTokenKey] = entry.Milliseconds, [RecordedChatClient.TotalKey] = entry.Milliseconds },
        };
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return update;
    }

    private string Key(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var model = options?.ModelId ?? InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId ?? "";
        var text = new StringBuilder($"{model}\u0001{options?.Temperature}\u0001{options?.MaxOutputTokens}\u0001{options?.Instructions?.ReplaceLineEndings("\n")}");
        foreach (var tool in options?.Tools ?? [])
        {
            text.Append($"\u0001tool {tool.Name}\u0002{tool.Description.ReplaceLineEndings("\n")}\u0002{(tool as AIFunctionDeclaration)?.JsonSchema.GetRawText()}");
        }

        foreach (var message in messages)
        {
            text.Append($"\u0001{message.Role}");
            foreach (var content in message.Contents)
            {
                text.Append(content switch
                {
                    TextContent t => $"\u0002{t.Text.ReplaceLineEndings("\n")}",
                    FunctionCallContent call => $"\u0002call {call.Name} {JsonSerializer.Serialize(call.Arguments, AIJsonUtilities.DefaultOptions)}",
                    FunctionResultContent result => $"\u0002result {(result.Result is string s ? s : JsonSerializer.Serialize(result.Result, AIJsonUtilities.DefaultOptions)).ReplaceLineEndings("\n")}",
                    _ => $"\u0002{content.GetType().Name}",
                });
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private sealed record Entry(string Key, string Messages, double Milliseconds, long InputTokens, long OutputTokens, DateTimeOffset At);
}
