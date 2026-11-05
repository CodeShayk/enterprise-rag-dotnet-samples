using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Generation;

/// <summary>
/// Records each streamed response, with its first-token and total time and the model's token counts, in a JSON Lines
/// file, and replays it for the next identical request. Experiments rerun without the model and still report the
/// latency the model had when it wrote each answer.
/// </summary>
public sealed class RecordedChatClient : DelegatingChatClient
{
    public const string FirstTokenKey = "recorded.firstTokenMs";
    public const string TotalKey = "recorded.totalMs";

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RecordedChatClient(IChatClient inner, string path) : base(inner)
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

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var key = Key(list, options);
        if (_entries.TryGetValue(key, out var entry))
        {
            Replayed++;
            var replay = new ChatResponseUpdate(ChatRole.Assistant, entry.Text)
            {
                AdditionalProperties = new() { [FirstTokenKey] = entry.FirstTokenMs, [TotalKey] = entry.TotalMs },
            };
            replay.Contents.Add(new UsageContent(new UsageDetails { InputTokenCount = entry.InputTokens, OutputTokenCount = entry.OutputTokens }));
            yield return replay;
            yield break;
        }

        var clock = Stopwatch.StartNew();
        var text = new StringBuilder();
        double? first = null;
        UsageDetails? usage = null;
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            if (first is null && !string.IsNullOrEmpty(update.Text)) first = clock.Elapsed.TotalMilliseconds;
            text.Append(update.Text);
            usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
            yield return update;
        }

        var total = clock.Elapsed.TotalMilliseconds;
        entry = new Entry(key, text.ToString(), first ?? total, total, usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0, DateTimeOffset.UtcNow);
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

    /// <summary>The model, the settings that change the reply and every message, with line endings normalized.</summary>
    private string Key(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var model = options?.ModelId ?? InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId ?? "";
        var format = options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema } ? schema.GetRawText() : options?.ResponseFormat?.GetType().Name ?? "";
        var text = new StringBuilder($"{model}\u0001{options?.Temperature}\u0001{options?.MaxOutputTokens}\u0001{format}");
        foreach (var message in messages) text.Append($"\u0001{message.Role}\u0002{message.Text.ReplaceLineEndings("\n")}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Reads the recorded first-token and total times from a replayed update, if it is one.</summary>
    public static (double FirstTokenMs, double TotalMs)? Timings(AdditionalPropertiesDictionary? properties) =>
        properties is not null && properties.TryGetValue(FirstTokenKey, out var first) && properties.TryGetValue(TotalKey, out var total)
            ? (Convert.ToDouble(first is JsonElement f ? f.GetDouble() : first), Convert.ToDouble(total is JsonElement t ? t.GetDouble() : total))
            : null;

    private sealed record Entry(string Key, string Text, double FirstTokenMs, double TotalMs, long InputTokens, long OutputTokens, DateTimeOffset At);
}
