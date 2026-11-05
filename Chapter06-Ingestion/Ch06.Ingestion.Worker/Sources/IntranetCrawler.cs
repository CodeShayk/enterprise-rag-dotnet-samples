using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Ch06.Ingestion.Worker.Store;
using Contoso.Knowledge.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Ch06.Ingestion.Worker.Sources;

/// <summary>
/// Crawls the intranet from its sitemap. Every page is requested with the ETag from the last run in
/// <c>If-None-Match</c>, so unchanged pages cost a 304 and no body; pages that return 404, or that left the sitemap,
/// are deletions. Real HTTP against <see cref="IntranetServer"/>, a local stand-in for the intranet.
/// </summary>
public sealed class IntranetCrawler(HttpClient http, IReadOnlyDictionary<string, RegisterEntry> register) : IConnector
{
    public string System => "intranet";

    public async IAsyncEnumerable<SourceItem> DiscoverAsync(IngestionStore store, DiscoveryStats stats, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sitemap = XDocument.Parse(await http.GetStringAsync("sitemap.xml", cancellationToken));
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loc in sitemap.Descendants(ns + "loc").Select(l => new Uri(l.Value)))
        {
            var uri = "intranet" + loc.AbsolutePath;
            seen.Add(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, loc.AbsolutePath.TrimStart('/'));
            if (store.State(System, uri)?.ETag is { } etag)
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
            }

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                stats.Add(System, "unchanged");
                continue;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                yield return new SourceItem(System, uri, MediaTypes.Html, DateTimeOffset.UtcNow, 0) { Deleted = true };
                continue;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            yield return new SourceItem(System, uri, MediaTypes.Html, response.Content.Headers.LastModified ?? DateTimeOffset.UtcNow, body.Length)
            {
                ETag = response.Headers.ETag?.ToString(),
                Content = body,
                Register = register.GetValueOrDefault(uri),
            };
        }

        foreach (var known in store.KnownUris(System).Where(u => !seen.Contains(u)))
        {
            yield return new SourceItem(System, known, MediaTypes.Html, DateTimeOffset.UtcNow, 0) { Deleted = true };
        }
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken) => Task.FromResult(item.Content!);
}

/// <summary>
/// A local stand-in for the intranet web server: static files with the ETag and Last-Modified headers and the
/// 304 responses that ASP.NET Core's static file middleware provides. Chapter 6 uses ports 5062 to 5069.
/// </summary>
public sealed class IntranetServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private IntranetServer(WebApplication app) => _app = app;

    public static async Task<IntranetServer> StartAsync(string folder, string url = "http://localhost:5062")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.GetFullPath(folder)),
            ServeUnknownFileTypes = false,
        });
        await app.StartAsync();
        return new IntranetServer(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
