using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Contoso.Knowledge.Generation;

/// <summary>What kind of reply the assistant gives.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnswerStatus>))]
public enum AnswerStatus
{
    /// <summary>The evidence answers the whole question.</summary>
    [JsonStringEnumMemberName("answered")] Answered,

    /// <summary>The evidence answers part of the question; a claim without a source says what's missing.</summary>
    [JsonStringEnumMemberName("partial")] Partial,

    /// <summary>The evidence doesn't answer the question.</summary>
    [JsonStringEnumMemberName("not_found")] NotFound,

    /// <summary>The question could mean several things that the evidence tells apart; the reply asks which.</summary>
    [JsonStringEnumMemberName("clarify")] Clarify,
}

/// <summary>What the model is asked to return. Its JSON schema goes with the request, so the reply always parses.</summary>
public sealed record ModelAnswer(
    [property: Description("answered, partial, not_found or clarify")] AnswerStatus Status,
    [property: Description("The answer as a list of claims, in the order the user should read them.")] IReadOnlyList<ModelClaim> Claims);

public sealed record ModelClaim(
    [property: Description("One sentence of the answer, written for the user.")] string Text,
    [property: Description("The id of the source the sentence comes from, such as S1; empty for a sentence that says what the sources don't contain.")] string Source,
    [property: Description("A short passage copied word for word from that source that supports the sentence; empty when the source is empty.")] string Quote);

/// <summary>A claim after validation: whether it cites a source that exists, whether its quote is in that source and where.</summary>
public sealed record Claim(string Text, string Source, string Quote, bool SourceExists, bool QuoteFound, string? DocumentId = null, int Start = -1, int End = -1)
{
    public bool Cited => Source.Length > 0;

    public bool Valid => SourceExists && QuoteFound;
}

/// <summary>Milliseconds from the question: evidence ready, first token, last token; and the model's own token counts.</summary>
public sealed record AnswerTimings(double RetrievalMs, double FirstTokenMs, double TotalMs, long PromptTokens, long OutputTokens);

/// <summary>
/// The assistant's typed answer: the kind of reply, its text with a marker for each claim's source, the claims after
/// validation, the evidence the model was given and a confidence the code computes: the share of claims whose quote
/// was found in the source they cite.
/// </summary>
public sealed record Answer(
    AnswerStatus Status,
    string Text,
    IReadOnlyList<Claim> Claims,
    IReadOnlyList<EvidenceBlock> Evidence,
    double Confidence,
    string PromptVersion,
    AnswerTimings Timings,
    IReadOnlyList<string> Degraded,
    IReadOnlyList<string> Problems)
{
    /// <summary>Every claim cites a source and quotes it; no problem was found.</summary>
    public bool Grounded => Claims.Count > 0 && Claims.All(c => c.Valid) && Problems.Count == 0;
}
