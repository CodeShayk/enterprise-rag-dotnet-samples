namespace Ch25.Knowledge.Api;

/// <summary>A question. Who asks comes from the validated bearer token, never from the body.</summary>
public sealed record AskRequest(string Question);

public sealed record ClaimReply(string Text, string Source, bool Verified, string? DocumentId);

public sealed record SourceReply(string Id, string DocumentId, string Title, string? Section, double Probability);

/// <summary>
/// The answer as a client receives it: its text and the safe HTML rendered from it, the claims and sources, what was
/// degraded, and what produced it (the index version, prompt, model and the provenance record's sequence number).
/// </summary>
public sealed record AnswerReply(
    string Status, string Text, string Html, IReadOnlyList<string> Removed, IReadOnlyList<ClaimReply> Claims, IReadOnlyList<SourceReply> Sources,
    IReadOnlyList<string> Degraded, string IndexVersion, string PromptVersion, string Model, bool Cached, long Provenance, double TotalMs);
