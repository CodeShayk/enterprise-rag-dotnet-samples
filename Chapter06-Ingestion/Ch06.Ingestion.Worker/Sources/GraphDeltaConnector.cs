using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ch06.Ingestion.Worker.Store;
using Contoso.Knowledge.Documents;

namespace Ch06.Ingestion.Worker.Sources;

/// <summary>
/// A SharePoint document library through Microsoft Graph delta queries. The first call lists everything; every
/// later call, made with the delta link the last one returned, lists only what changed since, deletions included.
/// Deleted items may come back with nothing but their ID and a <c>deleted</c> facet, so the connector keeps its own
/// map from item IDs to URIs in its checkpoint.
/// </summary>
public sealed class GraphDeltaConnector(HttpClient http, string driveId, IReadOnlyDictionary<string, RegisterEntry> register) : IConnector
{
    private readonly Dictionary<string, string> _downloads = [];
    private (string DeltaLink, Dictionary<string, string> Items)? _pending;

    public string System => "sharepoint";

    public async IAsyncEnumerable<SourceItem> DiscoverAsync(IngestionStore store, DiscoveryStats stats, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var checkpoint = store.Checkpoint(System) is { } json ? JsonSerializer.Deserialize<Checkpoint>(json)! : new Checkpoint(null, []);
        var items = new Dictionary<string, string>(checkpoint.Items);
        var url = checkpoint.DeltaLink ?? $"drives/{driveId}/root/delta";
        var changed = 0;
        while (true)
        {
            var page = await http.GetFromJsonAsync<JsonElement>(url, cancellationToken);
            foreach (var item in page.GetProperty("value").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                if (item.TryGetProperty("deleted", out _))
                {
                    if (items.Remove(id, out var gone))
                    {
                        changed++;
                        yield return new SourceItem(System, gone, MediaTypes.FromFileName(gone), DateTimeOffset.UtcNow, 0) { Deleted = true };
                    }

                    continue;
                }

                if (!item.TryGetProperty("file", out var file)) continue;   // folders
                var folder = item.GetProperty("parentReference").GetProperty("path").GetString()!.Split("root:", 2)[1];
                var uri = $"{System}{folder}/{item.GetProperty("name").GetString()}";
                items[id] = uri;
                _downloads[uri] = item.GetProperty("@microsoft.graph.downloadUrl").GetString()!;
                changed++;
                yield return new SourceItem(System, uri, file.GetProperty("mimeType").GetString()!,
                    item.GetProperty("lastModifiedDateTime").GetDateTimeOffset(), item.GetProperty("size").GetInt64())
                {
                    ETag = item.GetProperty("eTag").GetString(),
                    Register = register.GetValueOrDefault(uri),
                };
            }

            if (page.TryGetProperty("@odata.nextLink", out var next))
            {
                url = next.GetString()!;
                continue;
            }

            _pending = (page.GetProperty("@odata.deltaLink").GetString()!, items);
            break;
        }

        stats.Add(System, "unchanged", Math.Max(0, items.Count - changed));
    }

    public async Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken) =>
        await http.GetByteArrayAsync(_downloads[item.Uri], cancellationToken);

    public void Commit(IngestionStore store)
    {
        if (_pending is { } p) store.SaveCheckpoint(System, JsonSerializer.Serialize(new Checkpoint(p.DeltaLink, p.Items)));
    }

    private sealed record Checkpoint(string? DeltaLink, Dictionary<string, string> Items);
}

/// <summary>
/// A local stand-in for Microsoft Graph's delta endpoint over a folder, so that the connector runs without a tenant.
/// It pages results five at a time and keeps each delta token's snapshot on disk, as the service keeps its state.
/// Against a real tenant, the connector needs an app registration with the Sites.Selected permission and a handler
/// that adds a bearer token; that wasn't run for this book.
/// </summary>
public sealed class FakeGraphHandler(string root, string driveId) : HttpMessageHandler
{
    private const int PageSize = 5;
    private string Folder => Path.Combine(root, "sharepoint");
    private string Tokens => Path.Combine(root, ".graph-tokens.json");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        if (path.EndsWith("/content", StringComparison.Ordinal))
        {
            var id = path.Split('/')[^2];
            var file = Snapshot().Keys.First(f => Id(f) == id);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(Folder, file), cancellationToken)) };
        }

        var tokens = File.Exists(Tokens) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(await File.ReadAllTextAsync(Tokens, cancellationToken))! : [];
        var since = query["token"] is { Length: > 0 } token ? tokens[token] : [];
        var now = Snapshot();
        var changes = now.Where(f => !since.TryGetValue(f.Key, out var v) || v != f.Value).Select(f => Item(f.Key))
            .Concat(since.Keys.Where(f => !now.ContainsKey(f)).Select(f => new JsonObject { ["id"] = Id(f), ["deleted"] = new JsonObject { ["state"] = "deleted" } }))
            .ToList();

        var skip = int.TryParse(query["skip"], out var s) ? s : 0;
        var page = new JsonObject { ["value"] = new JsonArray([.. changes.Skip(skip).Take(PageSize)]) };
        var baseUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/delta";
        if (skip + PageSize < changes.Count)
        {
            page["@odata.nextLink"] = $"{baseUrl}?token={query["token"]}&skip={skip + PageSize}";
        }
        else
        {
            var next = $"t{tokens.Count + 1}";
            tokens[next] = now;
            await File.WriteAllTextAsync(Tokens, JsonSerializer.Serialize(tokens), cancellationToken);
            page["@odata.deltaLink"] = $"{baseUrl}?token={next}";
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    // Every file with a version stamp from its timestamp and size, as Graph's eTag changes when a file does.
    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(Folder, f).Replace('\\', '/'),
            f => $"{File.GetLastWriteTimeUtc(f).Ticks:x}-{new FileInfo(f).Length}");

    private JsonObject Item(string file)
    {
        var info = new FileInfo(Path.Combine(Folder, file));
        var folder = Path.GetDirectoryName(file)!.Replace('\\', '/');
        return new JsonObject
        {
            ["id"] = Id(file),
            ["name"] = info.Name,
            ["eTag"] = $"\"{{{Id(file)}}},{info.LastWriteTimeUtc.Ticks % 1000}\"",
            ["lastModifiedDateTime"] = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToString("O"),
            ["size"] = info.Length,
            ["parentReference"] = new JsonObject { ["driveId"] = driveId, ["path"] = $"/drives/{driveId}/root:/{folder}" },
            ["file"] = new JsonObject { ["mimeType"] = MediaTypes.FromFileName(file) },
            ["@microsoft.graph.downloadUrl"] = $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{Id(file)}/content",
        };
    }

    private static string Id(string file) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file)))[..16];
}
