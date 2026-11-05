using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;

namespace Ch14.Generation.Cli;

/// <summary>The answer pipelines this chapter compares, by name.</summary>
public sealed partial class Setups(IRetriever retriever, OnnxCrossEncoder crossEncoder, IChatClient chat)
{
    public static readonly string[] Names = ["chapter3", "grounded", "best-last", "compressed", "raw-tables", "drop-below-0.1"];

    public static string Describe(string name) => name switch
    {
        "chapter3" => "Chapter 3's prompt, four parents as retrieved",
        "grounded" => "assembled evidence, grounding prompt, quotes",
        "best-last" => "the same, strongest evidence last",
        "compressed" => "best sentences, 500 tokens",
        "raw-tables" => "table rows as Markdown, no column names",
        "drop-below-0.1" => "parents below P(relevant) 0.1 dropped",
        _ => throw new ArgumentException($"Unknown setup '{name}'."),
    };

    public GroundedAnswerGenerator Grounded(ContextOptions? context = null, ExtractiveCompressor? compressor = null) =>
        new(retriever, new ContextAssembler(Lab.Calibration, Lab.O200k, context), chat, Lab.O200k, compressor: compressor);

    public Func<string, Person, CancellationToken, Task<Answer>> Get(string name) => name switch
    {
        "chapter3" => Chapter3Async,
        "grounded" => Ask(Grounded()),
        "best-last" => Ask(Grounded(new ContextOptions { Order = EvidenceOrder.BestLast })),
        "compressed" => Ask(Grounded(compressor: new ExtractiveCompressor(crossEncoder, Lab.O200k, 500))),
        "raw-tables" => Ask(Grounded(new ContextOptions { TableRowsAsKeyValues = false })),
        "drop-below-0.1" => Ask(Grounded(new ContextOptions { MinProbability = 0.1 })),
        _ => throw new ArgumentException($"Unknown setup '{name}'."),
    };

    private static Func<string, Person, CancellationToken, Task<Answer>> Ask(GroundedAnswerGenerator generator) =>
        (question, asker, cancellationToken) => generator.AnswerAsync(question, asker, cancellationToken: cancellationToken);

    /// <summary>
    /// Chapter 3's pipeline as the baseline: the top four parents as retrieved, numbered, Chapter 3's instructions and a
    /// free-text answer. Its citations are the [n] markers in the text, and it abstains when it opens with a refusal.
    /// </summary>
    private async Task<Answer> Chapter3Async(string question, Person asker, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var result = await retriever.RetrieveAsync(new RetrievalRequest(question, asker, 4), cancellationToken);
        IReadOnlyList<EvidenceBlock> evidence = [.. result.Hits.Select((h, i) =>
        {
            var (doc, start, end) = h.Chunk.ReturnKey;
            return new EvidenceBlock($"{i + 1}", doc, h.Chunk.Document.Title, h.Chunk.Chunk.Header, h.Chunk.DocumentType, h.Chunk.EffectiveFrom,
                [ContextAssembler.Verbatim(h.Chunk.Document.Text, start, end)], Lab.Calibration.Probability(h.Score));
        })];
        var retrievalMs = clock.Elapsed.TotalMilliseconds;

        var prompt = new StringBuilder("Sources:\n");
        foreach (var block in evidence)
        {
            prompt.Append($"[{block.Id}] {block.Title} ({block.DocumentId})\n{BlankLines().Replace(block.Text, "\n")}\n\n");
        }

        prompt.Append($"Question: {question}");
        List<ChatMessage> messages = [new(ChatRole.System, Chapter3Instructions), new(ChatRole.User, prompt.ToString())];
        var (text, timings) = await Timed.CompleteAsync(chat, messages, new ChatOptions { Temperature = 0, MaxOutputTokens = 400 }, clock, retrievalMs, cancellationToken);

        // Each [n] marker counts as a claim citing source n; Chapter 3's prompt asks for no quotes.
        var cited = Numbers().Matches(text).Select(m => m.Groups[1].Value).Distinct()
            .Select(n => new Claim(text.Trim(), n, "", evidence.Any(b => b.Id == n), false, evidence.FirstOrDefault(b => b.Id == n)?.DocumentId))
            .ToList();
        var refused = Refusals.Any(r => text.TrimStart().StartsWith(r, StringComparison.OrdinalIgnoreCase));
        return new Answer(refused ? AnswerStatus.NotFound : AnswerStatus.Answered, text.Trim(), cited, evidence, 0, "chapter3", timings, result.Degraded, []);
    }

    private const string Chapter3Instructions =
        "You are the Contoso Knowledge Assistant. Answer only from the numbered sources. " +
        "Cite each source you use as [n]. If the sources don't answer the question, say that you don't know.";

    // Chapter 3's abstention test: an answer that opens with a refusal.
    private static readonly string[] Refusals = ["I don't know", "I do not know", "I couldn't find", "I can't answer", "The sources don't", "The sources do not"];

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex Numbers();
}

/// <summary>A completion through the recorded client, timed the way the generator times its own.</summary>
public static class Timed
{
    public static async Task<(string Text, AnswerTimings Timings)> CompleteAsync(
        IChatClient chat, IList<ChatMessage> messages, ChatOptions options, Stopwatch clock, double retrievalMs, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        (double First, UsageDetails? Usage, (double, double)? Recorded) seen = (0, null, null);
        await foreach (var update in chat.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (seen.First == 0 && !string.IsNullOrEmpty(update.Text)) seen.First = clock.Elapsed.TotalMilliseconds;
            seen.Usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? seen.Usage;
            seen.Recorded ??= RecordedChatClient.Timings(update.AdditionalProperties);
            text.Append(update.Text);
        }

        var (first, total) = seen.Recorded is (var f, var t) ? (retrievalMs + f, retrievalMs + t) : (seen.First, clock.Elapsed.TotalMilliseconds);
        return (text.ToString(), new AnswerTimings(retrievalMs, first, total, seen.Usage?.InputTokenCount ?? 0, seen.Usage?.OutputTokenCount ?? 0));
    }
}
