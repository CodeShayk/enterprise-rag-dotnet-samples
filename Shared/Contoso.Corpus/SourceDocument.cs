namespace Contoso.Corpus;

/// <summary>
/// One source document in Contoso's corpus. <see cref="AllowedGroups"/> lists the groups whose
/// members may read it; Chapter 22 replaces this list with permissions synced from the source system.
/// </summary>
public sealed record SourceDocument(
    string Id,
    string Title,
    string Tenant,
    IReadOnlyList<string> AllowedGroups,
    string Text);
