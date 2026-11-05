using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ch03.Knowledge;

/// <summary>What one model call cost: time to the first text, total time and tokens in and out.</summary>
public sealed record ChatMeasurement(
    TimeSpan TimeToFirstToken, TimeSpan Total, long InputTokens, long OutputTokens, bool TokensReported);

/// <summary>Where <see cref="MeasuringChatClient"/> puts its measurements. The baseline reads them.</summary>
public sealed class ChatMeasurements
{
    private ChatMeasurement? _last;

    public ChatMeasurement? Last => Volatile.Read(ref _last);

    public void Record(ChatMeasurement measurement) => Volatile.Write(ref _last, measurement);
}

/// <summary>
/// Middleware that measures every model call. It takes token counts from the provider's usage
/// report when there is one and counts them with the tokenizer when there isn't, and it says
/// which it did.
/// </summary>
public sealed class MeasuringChatClient(IChatClient inner, ChatMeasurements measurements, Tokenizer tokenizer)
    : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        TimeSpan? firstToken = null;
        UsageDetails? usage = null;
        var answer = new StringBuilder();

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (firstToken is null && !string.IsNullOrEmpty(update.Text))
            {
                firstToken = Stopwatch.GetElapsedTime(started);
            }

            answer.Append(update.Text);
            usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
            yield return update;
        }

        var total = Stopwatch.GetElapsedTime(started);
        measurements.Record(new ChatMeasurement(
            TimeToFirstToken: firstToken ?? total,
            Total: total,
            InputTokens: usage?.InputTokenCount ?? tokenizer.CountTokens(string.Join("\n", messages.Select(m => m.Text))),
            OutputTokens: usage?.OutputTokenCount ?? tokenizer.CountTokens(answer.ToString()),
            TokensReported: usage?.InputTokenCount is not null));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        var total = Stopwatch.GetElapsedTime(started);

        measurements.Record(new ChatMeasurement(
            TimeToFirstToken: total,
            Total: total,
            InputTokens: response.Usage?.InputTokenCount ?? tokenizer.CountTokens(string.Join("\n", messages.Select(m => m.Text))),
            OutputTokens: response.Usage?.OutputTokenCount ?? tokenizer.CountTokens(response.Text),
            TokensReported: response.Usage?.InputTokenCount is not null));

        return response;
    }
}
