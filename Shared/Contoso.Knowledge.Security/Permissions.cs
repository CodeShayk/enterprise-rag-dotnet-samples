using Contoso.Corpus;
using Contoso.Knowledge.Retrieval;

namespace Contoso.Knowledge.Security;

/// <summary>One permission change from the source, in the order the store saw them.</summary>
public sealed record AclChange(long Version, string DocumentId, IReadOnlySet<string> Groups);

/// <summary>
/// The source systems' permissions, kept apart from the indexes: each document's groups, a version that every change
/// raises and a change feed that index writers read to apply permission-only updates.
/// </summary>
public sealed class AclStore
{
    private readonly Dictionary<string, IReadOnlySet<string>> _groups = new(StringComparer.Ordinal);
    private readonly List<AclChange> _changes = [];
    private readonly Lock _lock = new();

    public AclStore(IEnumerable<SourceDocument> documents)
    {
        foreach (var d in documents) _groups[d.Id] = d.AllowedGroups.ToHashSet(StringComparer.Ordinal);
    }

    public long Version { get; private set; }

    public IReadOnlySet<string> GroupsOf(string documentId)
    {
        lock (_lock) return _groups.TryGetValue(documentId, out var groups) ? groups : new HashSet<string>();
    }

    public bool CanRead(Person asker, string documentId) => GroupsOf(documentId).Any(asker.Groups.Contains);

    public AclChange Set(string documentId, params string[] groups)
    {
        lock (_lock)
        {
            var change = new AclChange(++Version, documentId, groups.ToHashSet(StringComparer.Ordinal));
            _groups[documentId] = change.Groups;
            _changes.Add(change);
            return change;
        }
    }

    public IReadOnlyList<AclChange> ChangesSince(long version)
    {
        lock (_lock) return [.. _changes.Where(c => c.Version > version)];
    }

    /// <summary>The document with the store's current groups: what a permission-only update writes into an index.</summary>
    public SourceDocument Apply(SourceDocument document) => document with { AllowedGroups = [.. GroupsOf(document.Id)] };
}

/// <summary>
/// The second check: every hit an index returns is checked again against the live permissions before anything reaches
/// a prompt, a cache or a log. An index that's minutes behind its source leaks in that window without it.
/// </summary>
public static class PermissionCheck
{
    public static (IReadOnlyList<Hit> Allowed, IReadOnlyList<Hit> Blocked) Verify(Person asker, IReadOnlyList<Hit> hits, AclStore acls)
    {
        var allowed = hits.Where(h => acls.CanRead(asker, h.Chunk.Document.Id)).ToList();
        return (allowed, [.. hits.Except(allowed)]);
    }
}

/// <summary>A retriever that verifies its inner retriever's hits against the live permissions and reports what it blocked.</summary>
public sealed class VerifiedRetriever(IRetriever inner, AclStore acls) : IRetriever
{
    public string Name => inner.Name;

    public int Blocked { get; private set; }

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var result = await inner.RetrieveAsync(request, cancellationToken);
        var (allowed, blocked) = PermissionCheck.Verify(request.Asker, result.Hits, acls);
        Blocked += blocked.Count;
        return result with { Hits = allowed };
    }
}
