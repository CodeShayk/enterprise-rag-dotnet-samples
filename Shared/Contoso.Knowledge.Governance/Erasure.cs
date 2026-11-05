namespace Contoso.Knowledge.Governance;

/// <summary>
/// A request to remove one source document from the assistant: why (the legal basis), who asked, and probe strings
/// (a name, an address) that would betray a copy of its text in any store, whatever the copy is called there.
/// </summary>
public sealed record ErasureRequest(string Id, string DocumentId, string Basis, string RequestedBy, IReadOnlyList<string> Probes);

/// <summary>A store that holds copies of source text or things derived from it, and can find and erase them.</summary>
public interface IErasableStore
{
    string Name { get; }

    /// <summary>Entries that name the document or contain any probe string.</summary>
    Task<int> CountAsync(ErasureRequest request, CancellationToken cancellationToken = default);

    Task<int> EraseAsync(ErasureRequest request, CancellationToken cancellationToken = default);
}

public sealed record StoreResult(string Store, int Before, int Erased, int After);

/// <summary>What stays after an erasure: identifiers and a hash, enough to block the text's return and to prove the erasure, nothing more.</summary>
public sealed record Tombstone(string DocumentId, string ContentSha, string RequestId, string Basis, DateTimeOffset ErasedAt);

/// <summary>The workflow's answer: erased with a count from every store before and after, or refused and why.</summary>
public sealed record ErasureCertificate(ErasureRequest Request, string Outcome, IReadOnlyList<StoreResult> Stores, Tombstone? Tombstone)
{
    public bool Proven => Tombstone is not null && Stores.All(s => s.After == 0);
}

/// <summary>Documents preserved for a legal matter: neither retention nor an erasure request may remove them while the hold stands.</summary>
public sealed class LegalHolds
{
    private readonly Dictionary<string, string> _held = new(StringComparer.Ordinal);

    public void Place(string documentId, string matter) => _held[documentId] = matter;

    public void Release(string documentId) => _held.Remove(documentId);

    public string? MatterFor(string documentId) => _held.GetValueOrDefault(documentId);
}

/// <summary>Tombstones by document and by content, so a connector that offers the same text again is turned away.</summary>
public sealed class TombstoneRegistry
{
    private readonly List<Tombstone> _tombstones = [];

    public IReadOnlyList<Tombstone> All => _tombstones;

    public void Add(Tombstone tombstone) => _tombstones.Add(tombstone);

    public Tombstone? Blocking(string documentId, string contentSha) =>
        _tombstones.FirstOrDefault(t => t.DocumentId == documentId || t.ContentSha == contentSha);
}

/// <summary>
/// Erasure that proves itself: refuse a held document; otherwise count the copies in every store, erase them, count
/// again and leave a tombstone. A store that still finds a copy afterward makes the certificate unproven.
/// </summary>
public sealed class ErasureWorkflow(IReadOnlyList<IErasableStore> stores, LegalHolds holds, TombstoneRegistry tombstones, TimeProvider clock)
{
    public async Task<ErasureCertificate> RunAsync(ErasureRequest request, string contentSha, CancellationToken cancellationToken = default)
    {
        if (holds.MatterFor(request.DocumentId) is { } matter) return new ErasureCertificate(request, $"refused: legal hold ({matter})", [], null);

        var results = new List<StoreResult>();
        foreach (var store in stores)
        {
            var before = await store.CountAsync(request, cancellationToken);
            var erased = await store.EraseAsync(request, cancellationToken);
            results.Add(new StoreResult(store.Name, before, erased, await store.CountAsync(request, cancellationToken)));
        }

        var tombstone = new Tombstone(request.DocumentId, contentSha, request.Id, request.Basis, clock.GetUtcNow());
        tombstones.Add(tombstone);
        return new ErasureCertificate(request, "erased", results, tombstone);
    }
}

/// <summary>Something the assistant keeps for a while (a captured question and answer, a trace) and the documents it concerns.</summary>
public sealed record RetainedItem(string Id, string Kind, DateTimeOffset Created, IReadOnlyList<string> Documents);

public sealed record RetentionDecision(string Id, string Decision);

public static class RetentionJob
{
    /// <summary>Items older than their kind's period are due for erasure, unless they concern a document under legal hold.</summary>
    public static IReadOnlyList<RetentionDecision> Review(IEnumerable<RetainedItem> items, IReadOnlyDictionary<string, TimeSpan> keep, LegalHolds holds, DateTimeOffset now) =>
        [.. items.Select(item =>
        {
            if (!keep.TryGetValue(item.Kind, out var period) || now - item.Created < period) return new RetentionDecision(item.Id, "keep");
            var matter = item.Documents.Select(holds.MatterFor).FirstOrDefault(m => m is not null);
            return new RetentionDecision(item.Id, matter is null ? "erase" : $"held ({matter})");
        })];
}
