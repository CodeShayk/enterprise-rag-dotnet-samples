using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Correction;

/// <summary>
/// One claim after verification: how strongly the block it cites entails it, and the best other block of the same
/// evidence when that one doesn't. A claim another block supports has the right fact under the wrong citation.
/// </summary>
public sealed record ClaimCheck(int Index, double Support, bool Supported, string? BetterSource, double BetterSupport)
{
    public bool Repairable => !Supported && BetterSource is not null;
}

public sealed record Verification(IReadOnlyList<ClaimCheck> Checks)
{
    public int Supported => Checks.Count(c => c.Supported);

    public int Repairable => Checks.Count(c => c.Repairable);

    public int Unsupported => Checks.Count(c => !c.Supported && !c.Repairable);
}

/// <summary>
/// Post-generation checks with Chapter 15's NLI detector. Every claim that names a source is checked against that
/// block; a claim below the threshold is checked against every other block, so it can be re-cited instead of dropped.
/// Moving a citation asserts support, so it needs a stronger score than keeping one: <paramref name="reciteFrom"/>.
/// </summary>
public sealed class AnswerVerifier(ClaimSupport support, double threshold = 0.05, double reciteFrom = 0.5)
{
    public double Threshold => threshold;

    public double ReciteFrom => reciteFrom;

    public Verification Verify(Answer answer)
    {
        var blocks = answer.Evidence.ToDictionary(b => b.Id, Premise);
        var factual = answer.Claims.Select((c, i) => (Claim: c, Index: i)).Where(x => x.Claim.Cited || answer.Status is AnswerStatus.Answered).ToList();
        var cited = support.Score([.. factual.Select(x => (blocks.GetValueOrDefault(x.Claim.Source, ""), x.Claim.Text))]);

        // Claims their own block doesn't support, against every other block.
        var others = factual.Where((_, k) => cited[k] < threshold)
            .SelectMany(x => answer.Evidence.Where(b => b.Id != x.Claim.Source).Select(b => (x.Index, Block: b.Id, Premise: blocks[b.Id], x.Claim.Text))).ToList();
        var elsewhere = support.Score([.. others.Select(o => (o.Premise, o.Text))]);
        var best = others.Zip(elsewhere).GroupBy(p => p.First.Index).ToDictionary(g => g.Key, g => g.MaxBy(p => p.Second));

        return new Verification([.. factual.Select((x, k) => cited[k] >= threshold
            ? new ClaimCheck(x.Index, cited[k], true, null, 0)
            : best.TryGetValue(x.Index, out var b) && b.Second >= reciteFrom
                ? new ClaimCheck(x.Index, cited[k], false, b.First.Block, b.Second)
                : new ClaimCheck(x.Index, cited[k], false, null, best.TryGetValue(x.Index, out var w) ? w.Second : 0))]);
    }

    /// <summary>
    /// Re-cites what another block supports and drops what nothing supports. An answer left with no factual claim
    /// becomes not found; the abstention policy decides what the user reads.
    /// </summary>
    public Answer Repair(Answer answer, Verification verification)
    {
        var checks = verification.Checks.ToDictionary(c => c.Index);
        var claims = new List<Claim>();
        var notes = new List<string>(answer.Problems);
        for (var i = 0; i < answer.Claims.Count; i++)
        {
            var claim = answer.Claims[i];
            if (!checks.TryGetValue(i, out var check) || check.Supported)
            {
                claims.Add(claim);
            }
            else if (check.Repairable)
            {
                var block = answer.Evidence.First(b => b.Id == check.BetterSource);
                claims.Add(claim with { Source = block.Id, Quote = "", SourceExists = true, QuoteFound = false, DocumentId = block.DocumentId, Start = -1, End = -1 });
                notes.Add($"re-cited \"{claim.Text}\" from {(claim.Cited ? claim.Source : "nothing")} to {block.Id}");
            }
            else
            {
                notes.Add($"dropped \"{claim.Text}\": no block supports it");
            }
        }

        var factual = claims.Count(c => c.Cited || answer.Status is AnswerStatus.Answered);
        var status = factual == 0 && answer.Status is AnswerStatus.Answered or AnswerStatus.Partial ? AnswerStatus.NotFound : answer.Status;
        var text = claims.Count == 0 ? GroundedAnswerGenerator.NotFoundText : CitationValidator.Render(claims);
        var supported = verification.Supported + verification.Repairable;
        return answer with { Status = status, Text = text, Claims = claims, Confidence = verification.Checks.Count == 0 ? 0 : supported / (double)verification.Checks.Count, Problems = notes };
    }

    /// <summary>The premise a claim is checked against: the section path the generator saw, then the block's text.</summary>
    public static string Premise(EvidenceBlock block) => $"{block.Section}\n{block.Text}";
}

/// <summary>
/// Regenerate with feedback: the same grounding prompt, the model's first answer and a message that names the claims
/// no source supports. One more call; the new answer is verified like the first.
/// </summary>
public sealed class FeedbackRegenerator(IChatClient chat, GenerationOptions? options = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web);

    private readonly GenerationOptions _options = options ?? new GenerationOptions();

    public async Task<(Answer Answer, double WallSeconds)> RegenerateAsync(
        string question, Person asker, Answer first, Verification verification, CancellationToken cancellationToken = default)
    {
        var messages = GroundingPrompt.Build(question, asker, first.Evidence, _options.Today);
        messages.Add(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new ModelAnswer(first.Status, [.. first.Claims.Select(c => new ModelClaim(c.Text, c.Source, c.Quote))]), Json)));
        messages.Add(new ChatMessage(ChatRole.User, Feedback(first, verification)));
        var chatOptions = new ChatOptions { Temperature = 0, MaxOutputTokens = _options.MaxOutputTokens, ResponseFormat = GroundedAnswerGenerator.Format };

        var clock = Stopwatch.StartNew();
        var reply = new StringBuilder();
        (UsageDetails? Usage, (double First, double Total)? Recorded) seen = (null, null);
        await foreach (var update in chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
        {
            seen.Usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? seen.Usage;
            seen.Recorded ??= RecordedChatClient.Timings(update.AdditionalProperties);
            reply.Append(update.Text);
        }

        var wall = clock.Elapsed.TotalSeconds;
        var total = seen.Recorded?.Total ?? wall * 1000;
        var timings = new AnswerTimings(0, seen.Recorded?.First ?? total, total, seen.Usage?.InputTokenCount ?? 0, seen.Usage?.OutputTokenCount ?? 0);
        return (GroundedAnswerGenerator.Validate(reply.ToString(), first.Evidence, first.Degraded, timings), wall);
    }

    /// <summary>The feedback message: the claims nothing in the evidence supports, and what to do about them.</summary>
    public static string Feedback(Answer first, Verification verification)
    {
        var text = new StringBuilder("Check your answer against the sources. No source supports these claims:\n");
        foreach (var check in verification.Checks.Where(c => !c.Supported && !c.Repairable))
        {
            var claim = first.Claims[check.Index];
            text.Append($"- \"{claim.Text}\"{(claim.Cited ? $" ({claim.Source})" : "")}\n");
        }

        return text.Append("Answer the question again from the sources. Keep the claims they support, correct or leave out the others, and give each claim the source and quote that support it.").ToString();
    }
}
