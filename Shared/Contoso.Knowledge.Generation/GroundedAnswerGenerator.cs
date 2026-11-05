using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contoso.Corpus;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Generation;

/// <summary>What the generator reports while it works: the evidence, pieces of the answer's text, the typed answer.</summary>
public abstract record AnswerEvent;

public sealed record EvidenceReady(IReadOnlyList<EvidenceBlock> Evidence, IReadOnlyList<string> Degraded) : AnswerEvent;

public sealed record AnswerDelta(string Text) : AnswerEvent;

public sealed record AnswerReady(Answer Answer) : AnswerEvent;

public sealed record GenerationOptions
{
    /// <summary>Parents taken from the retriever; Chapter 12's re-ranker chooses them from eight.</summary>
    public int Top { get; init; } = 4;

    public int MaxOutputTokens { get; init; } = 700;

    /// <summary>The tokens of conversation a prompt may carry.</summary>
    public int HistoryTokens { get; init; } = 400;

    /// <summary>Contoso's "today" in all of its data; a real deployment reads a <see cref="TimeProvider"/>.</summary>
    public DateOnly Today { get; init; } = new(2027, 2, 1);
}

/// <summary>
/// Retrieves, assembles the evidence, abstains when nothing relevant was found or asks the model, then validates. The
/// answer comes back typed, with every citation checked against the evidence and a confidence computed from the
/// evidence its valid citations point to.
/// </summary>
public sealed class GroundedAnswerGenerator(
    IRetriever retriever, ContextAssembler assembler, IChatClient chat, Tokenizer tokenizer,
    GenerationOptions? options = null, ExtractiveCompressor? compressor = null)
{
    public const string NotFoundText = "I couldn't find this in the documents you can read.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web);

    /// <summary>The reply's JSON schema, sent with every request; a regeneration with feedback (Chapter 16) sends the same.</summary>
    public static readonly ChatResponseFormat Format =
        ChatResponseFormat.ForJsonSchema(Schema(), "grounded_answer", "An answer from the sources as claims, each with its source and a quote.");

    public GenerationOptions Options { get; } = options ?? new GenerationOptions();

    public async Task<Answer> AnswerAsync(string question, Person asker, IReadOnlyList<Turn>? history = null, CancellationToken cancellationToken = default)
    {
        await foreach (var e in StreamAsync(question, asker, history, cancellationToken: cancellationToken))
        {
            if (e is AnswerReady ready) return ready.Answer;
        }

        throw new InvalidOperationException("The generator finished without an answer.");
    }

    /// <summary>
    /// Answers a question as a stream of events. With <paramref name="evidence"/> given, retrieval is skipped: that's how
    /// "make that shorter" reworks the previous answer from the evidence it already had.
    /// </summary>
    public async IAsyncEnumerable<AnswerEvent> StreamAsync(
        string question, Person asker, IReadOnlyList<Turn>? history = null, IReadOnlyList<EvidenceBlock>? evidence = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        using var span = GenerationTelemetry.Source.StartActivity("answer");
        span?.SetTag("rag.question.length", question.Length);
        span?.SetTag("rag.history.turns", history?.Count ?? 0);
        IReadOnlyList<string> degraded = [];
        var reworking = evidence is not null && history is { Count: > 0 };
        if (evidence is null)
        {
            // A follow-up is searched together with the question before it (Chapter 13); the prompt gets the turns.
            var query = history is { Count: > 0 } ? $"{history[^1].Question} {question}" : question;
            var result = await retriever.RetrieveAsync(new RetrievalRequest(query, asker, Options.Top), cancellationToken);
            AssembledContext context;
            using (var assemble = GenerationTelemetry.Source.StartActivity("assemble"))
            {
                context = assembler.Assemble(result.Hits);
                assemble?.SetTag("rag.evidence.blocks", context.Blocks.Count);
                assemble?.SetTag("rag.evidence.tokens", context.Tokens);
                assemble?.SetTag("rag.evidence.best_probability", context.BestProbability);
            }

            if (compressor is not null && !context.IsEmpty)
            {
                using var compress = GenerationTelemetry.Source.StartActivity("compress");
                compress?.SetTag("rag.compress.tokens_in", context.Tokens);
                context = compressor.Compress(question, context);
                compress?.SetTag("rag.compress.tokens_out", context.Tokens);
            }

            (evidence, degraded) = (context.Blocks, result.Degraded);
        }

        var retrievalMs = clock.Elapsed.TotalMilliseconds;
        yield return new EvidenceReady(evidence, degraded);

        // An async iterator resumes in its consumer's context, so the answer span is made current again after each yield.
        if (span is not null) Activity.Current = span;

        // Nothing the asker may read is relevant enough: say so without asking the model, in words that don't reveal
        // whether the answer doesn't exist or exists where the asker can't see it.
        if (evidence.Count == 0)
        {
            var none = new Answer(AnswerStatus.NotFound, NotFoundText, [], [], 0, "none", new AnswerTimings(retrievalMs, retrievalMs, retrievalMs, 0, 0), degraded, []);
            GenerationTelemetry.Tag(span, none);
            yield return new AnswerReady(none);
            yield break;
        }

        // A rework ("make that shorter") isn't a question about the documents; say what to do with the last answer.
        var asked = reworking ? $"Rewrite your last answer as I ask: \"{question}\" Keep its facts, sources and quotes." : question;
        var window = HistoryWindow.Of(history ?? [], tokenizer, Options.HistoryTokens);
        var messages = GroundingPrompt.Build(asked, asker, evidence, Options.Today, window);
        var chatOptions = new ChatOptions { Temperature = 0, MaxOutputTokens = Options.MaxOutputTokens, ResponseFormat = Format };

        var field = new StreamingJsonField("text");
        var reply = new StringBuilder();
        (double First, UsageDetails? Usage, (double, double)? Recorded) seen = (0, null, null);
        await foreach (var update in chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
        {
            if (seen.First == 0 && !string.IsNullOrEmpty(update.Text)) seen.First = clock.Elapsed.TotalMilliseconds;
            seen.Usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? seen.Usage;
            seen.Recorded ??= RecordedChatClient.Timings(update.AdditionalProperties);
            reply.Append(update.Text);
            var delta = field.Push(update.Text);
            if (delta.Length > 0) yield return new AnswerDelta(delta);
        }

        // A replayed call reports the times the model took when the call was recorded.
        var (first, total) = seen.Recorded is (var f, var t) ? (retrievalMs + f, retrievalMs + t) : (seen.First, clock.Elapsed.TotalMilliseconds);
        var timings = new AnswerTimings(retrievalMs, first, total, seen.Usage?.InputTokenCount ?? 0, seen.Usage?.OutputTokenCount ?? 0);
        if (span is not null) Activity.Current = span;
        yield return new AnswerReady(Validate(reply.ToString(), evidence, degraded, timings));
    }

    /// <summary>The JSON schema of <see cref="ModelAnswer"/>, with at least one claim: an empty list once came back as an answer.</summary>
    private static JsonElement Schema()
    {
        var schema = JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(typeof(ModelAnswer), serializerOptions: Json).GetRawText())!;
        schema["properties"]!["claims"]!["minItems"] = 1;
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>Parses the model's JSON, checks its claims and writes the text; confidence is the share of claims verified.</summary>
    public static Answer Validate(string reply, IReadOnlyList<EvidenceBlock> evidence, IReadOnlyList<string> degraded, AnswerTimings timings)
    {
        using var span = GenerationTelemetry.Source.StartActivity("validate");
        var answer = Check(reply, evidence, degraded, timings);
        span?.SetTag("rag.claims", answer.Claims.Count);
        span?.SetTag("rag.claims.valid", answer.Claims.Count(c => c.Valid));
        span?.SetTag("rag.problems", answer.Problems.Count);

        // Validation inside an answer span gives that answer its verdict.
        if (span?.Parent is { OperationName: "answer" } answerSpan && answerSpan.Source == GenerationTelemetry.Source) GenerationTelemetry.Tag(answerSpan, answer);
        return answer;
    }

    private static Answer Check(string reply, IReadOnlyList<EvidenceBlock> evidence, IReadOnlyList<string> degraded, AnswerTimings timings)
    {
        ModelAnswer? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ModelAnswer>(reply, Json);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null)
        {
            return new Answer(AnswerStatus.NotFound, NotFoundText, [], evidence, 0, GroundingPrompt.Version, timings, degraded, ["the reply wasn't the JSON the schema asks for"]);
        }

        var (claims, problems) = CitationValidator.Validate(parsed, evidence);

        // A partial answer's uncited claim says what's missing; every other claim should be backed by a quote.
        var factual = claims.Where(c => c.Cited || parsed.Status is AnswerStatus.Answered).ToList();
        var confidence = parsed.Status is AnswerStatus.Answered or AnswerStatus.Partial && factual.Count > 0
            ? factual.Count(c => c.Valid) / (double)factual.Count
            : 0;
        return new Answer(parsed.Status, CitationValidator.Render(claims), claims, evidence, confidence, GroundingPrompt.Version, timings, degraded, problems);
    }
}
