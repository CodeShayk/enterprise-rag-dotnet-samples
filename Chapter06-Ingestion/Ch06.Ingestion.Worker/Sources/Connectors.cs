using System.Globalization;
using System.Runtime.CompilerServices;
using Ch06.Ingestion.Worker.Store;
using Contoso.Knowledge.Documents;

namespace Ch06.Ingestion.Worker.Sources;

/// <summary>One item a connector found new, changed or deleted, with what its source system reports about it.</summary>
public sealed record SourceItem(string System, string Uri, string MediaType, DateTimeOffset LastModified, long Length)
{
    public string? ETag { get; init; }

    /// <summary>The register entry: the metadata the source system keeps for this file.</summary>
    public RegisterEntry? Register { get; init; }

    /// <summary>The bytes, when discovering the item already fetched them (an HTTP 200, a database row).</summary>
    public byte[]? Content { get; init; }

    public bool Deleted { get; init; }

    /// <summary>For a file found inside another one, such as an email attachment: the outer file's URI.</summary>
    public string? Parent { get; init; }
}

/// <summary>
/// A connector reports what changed in its source since the last run, using the cheapest signal the source offers
/// (a timestamp and size, an ETag, a delta token, a watermark), and fetches an item's bytes on demand.
/// </summary>
public interface IConnector
{
    string System { get; }

    IAsyncEnumerable<SourceItem> DiscoverAsync(IngestionStore store, DiscoveryStats stats, CancellationToken cancellationToken);

    Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken);

    /// <summary>Saves the connector's new checkpoint once the run has succeeded, so that a failed run is repeated.</summary>
    void Commit(IngestionStore store)
    {
    }
}

/// <summary>Counts per source system for the run report.</summary>
public sealed class DiscoveryStats
{
    private readonly Dictionary<string, Dictionary<string, int>> _counts = [];

    public void Add(string system, string kind, int n = 1)
    {
        lock (_counts)
        {
            if (!_counts.TryGetValue(system, out var bySystem)) _counts[system] = bySystem = [];
            bySystem[kind] = bySystem.GetValueOrDefault(kind) + n;
        }
    }

    public int Get(string system, string kind)
    {
        lock (_counts) return _counts.GetValueOrDefault(system)?.GetValueOrDefault(kind) ?? 0;
    }

    public IEnumerable<string> Systems
    {
        get
        {
            lock (_counts) return [.. _counts.Keys];
        }
    }
}

/// <summary>
/// A file share (a folder here). The cheap check is the timestamp and size the share reports: if both match
/// the last run, the file isn't read at all. Anything else is fetched and hashed, because a timestamp also changes
/// when someone opens and re-saves a file without changing it.
/// </summary>
public sealed class FileShareConnector(string root, string system, IReadOnlyDictionary<string, RegisterEntry> register) : IConnector
{
    public string System => system;

    public async IAsyncEnumerable<SourceItem> DiscoverAsync(IngestionStore store, DiscoveryStats stats, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, system);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (uri.EndsWith(".json", StringComparison.Ordinal) || uri.EndsWith(".xml", StringComparison.Ordinal)) continue;
            seen.Add(uri);
            var info = new FileInfo(path);
            var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            var previous = store.State(system, uri);
            if (previous is not null && previous.LastModified == modified && previous.Length == info.Length)
            {
                stats.Add(system, "unchanged");
                continue;
            }

            yield return new SourceItem(system, uri, MediaTypes.FromFileName(path), modified, info.Length) { Register = register.GetValueOrDefault(uri) };
        }

        foreach (var known in store.KnownUris(system).Where(u => !seen.Contains(u) && !u.Contains('#')))
        {
            yield return new SourceItem(system, known, MediaTypes.FromFileName(known), DateTimeOffset.UtcNow, 0) { Deleted = true };
        }

        await Task.CompletedTask;
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(Path.Combine(root, item.Uri), cancellationToken);
}

/// <summary>
/// The service mailbox export: one EML file per thread. Attachments come out of the parser and are ingested as
/// documents of their own, linked to the message.
/// </summary>
public sealed class MailboxConnector(string root, IReadOnlyDictionary<string, RegisterEntry> register) : IConnector
{
    private readonly FileShareConnector _files = new(root, "mail", register);

    public string System => "mail";

    public IAsyncEnumerable<SourceItem> DiscoverAsync(IngestionStore store, DiscoveryStats stats, CancellationToken cancellationToken) =>
        _files.DiscoverAsync(store, stats, cancellationToken);

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken) => _files.FetchAsync(item, cancellationToken);
}

/// <summary>
/// The maintenance system's work orders, read with a watermark on <c>updated_at</c>: each run asks only for rows
/// changed since the last one. A watermark can't see deleted rows; that needs a soft-delete flag or change data capture.
/// Here the table is the nightly CSV export; each changed row becomes a small Markdown document.
/// </summary>
public sealed class DatabaseConnector(string root, string groups = "fluid,motion,service") : IConnector
{
    public string System => "database";

    public async IAsyncEnumerable<SourceItem> DiscoverAsync(IngestionStore store, DiscoveryStats stats, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var watermark = store.Checkpoint(System) is { } value ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue;
        var rows = Contoso.Knowledge.Parsing.CsvParser.Parse(await File.ReadAllTextAsync(Path.Combine(root, "database", "work-orders.csv"), cancellationToken));
        var header = rows[0].ToList();
        var newest = watermark;
        foreach (var row in rows.Skip(1))
        {
            var updated = DateTimeOffset.Parse(row[header.IndexOf("updated_at")], CultureInfo.InvariantCulture);
            if (updated <= watermark)
            {
                stats.Add(System, "unchanged");
                continue;
            }

            if (updated > newest) newest = updated;
            var markdown = $"# Work order {row[0]}\n\n| Field | Value |\n|---|---|\n" +
                string.Concat(header.Skip(1).Select((h, i) => $"| {h} | {row[i + 1]} |\n"));
            var bytes = global::System.Text.Encoding.UTF8.GetBytes(markdown);
            var uri = $"database/work_orders/{row[0]}";
            // A row's metadata comes from the table: the asset's tenant decides who may read it.
            var metadata = global::System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                title = $"Work order {row[0]} ({row[1]})",
                tenant = "service",
                documentType = DocumentTypes.WorkOrders,
                sensitivity = SensitivityLabels.Internal,
                allowedGroups = groups.Split(','),
                owner = "Maintenance systems",
                effectiveFrom = row[header.IndexOf("opened")],
                equipmentTags = new[] { row[1] },
            });
            yield return new SourceItem(System, uri, MediaTypes.Markdown, updated, bytes.Length)
            {
                Content = bytes,
                // Rows change in place, so a row is one document version that each update replaces.
                Register = new RegisterEntry(uri, System, row[0], "1", MediaTypes.Markdown, updated, bytes.Length, "", metadata, ""),
            };
        }

        _pending = newest;
    }

    private DateTimeOffset? _pending;

    public void Commit(IngestionStore store)
    {
        if (_pending is { } watermark) store.SaveCheckpoint(System, watermark.ToString("O", CultureInfo.InvariantCulture));
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken) => Task.FromResult(item.Content!);
}
