using Ganss.Xss;
using Markdig;

namespace Contoso.Knowledge.Security;

/// <summary>What safe rendering removed from an answer.</summary>
public sealed record RenderedAnswer(string Html, IReadOnlyList<string> Removed);

/// <summary>
/// An answer rendered for a browser: Markdown to HTML with raw HTML off, then an allow-list of tags, no images, and
/// links only to hosts Contoso trusts. A model that was talked into writing an image whose address carries the
/// conversation can't make the browser fetch it. Each call gets its own sanitizer, so concurrent requests never share
/// a list of removals.
/// </summary>
public sealed class SafeRenderer(IReadOnlySet<string> allowedHosts)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    /// <summary>The answer as Markdig renders it by default, raw HTML included: what a page that trusts the model would show.</summary>
    public static string Unsafe(string answer) => Markdown.ToHtml(answer);

    public RenderedAnswer Render(string answer)
    {
        var removed = new List<string>();
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(["p", "a", "ul", "ol", "li", "strong", "em", "code", "pre", "br"], StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(["href"], StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(["https"], StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(["href", "src"], StringComparer.OrdinalIgnoreCase),
        });
        sanitizer.RemovingTag += (_, e) => removed.Add($"<{e.Tag.TagName.ToLowerInvariant()}> {e.Tag.GetAttribute("src") ?? e.Tag.GetAttribute("href")}".TrimEnd());
        sanitizer.FilterUrl += (_, e) =>
        {
            if (Uri.TryCreate(e.OriginalUrl, UriKind.Absolute, out var uri) && allowedHosts.Contains(uri.Host)) return;
            removed.Add($"link {e.OriginalUrl}");
            e.SanitizedUrl = null;
        };

        var html = sanitizer.Sanitize(Markdown.ToHtml(answer, Pipeline));
        return new RenderedAnswer(html.Trim(), removed);
    }
}
