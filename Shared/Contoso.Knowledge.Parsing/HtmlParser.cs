using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Contoso.Knowledge.Documents;

namespace Contoso.Knowledge.Parsing;

public sealed record HtmlParserOptions
{
    /// <summary>
    /// Keep only the page's main content: the <c>main</c> or <c>article</c> element, or else the element with the
    /// most paragraph text. Turn it off to get the whole page as unlabeled blocks, navigation and footer included
    /// (Chapter 6 does, to find the site's template by its repetition across pages).
    /// </summary>
    public bool MainContentOnly { get; init; } = true;
}

/// <summary>
/// Intranet pages through AngleSharp, which parses HTML the way browsers do and never runs scripts.
/// Headings, paragraphs, lists and tables keep their structure; the page's title and meta tags become properties.
/// </summary>
public sealed class HtmlParser(HtmlParserOptions? options = null) : IDocumentParser
{
    private readonly HtmlParserOptions _options = options ?? new HtmlParserOptions();

    private static readonly HashSet<string> Boilerplate = new(StringComparer.OrdinalIgnoreCase) { "nav", "header", "footer", "aside", "form", "script", "style", "noscript", "template" };

    public string Name => "html";

    public string Version => _options.MainContentOnly ? "1.0" : "1.0-full";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Html];

    public async Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await reader.ReadToEndAsync(cancellationToken));
        var properties = document.Head?.QuerySelectorAll("meta[name][content]")
            .ToDictionary(m => m.GetAttribute("name")!, m => m.GetAttribute("content")!, StringComparer.OrdinalIgnoreCase)
            ?? [];

        var root = _options.MainContentOnly ? MainContent(document) : document.Body;
        var blocks = new List<Block>();
        if (root is not null) Walk(root, blocks, listLevel: 0, insideMain: _options.MainContentOnly || IsMain(root));

        // Links stay as text in their paragraphs; their targets go into the properties, as relations Chapter 19 can use.
        var links = root?.QuerySelectorAll("a[href]").Select(a => $"{TextCleanup.Normalize(a.TextContent)} -> {a.GetAttribute("href")}").Distinct().ToList() ?? [];
        if (links.Count > 0) properties["links"] = string.Join('\n', links);

        var title = document.Title is { } t ? t.Split(" | ")[0].Trim() : blocks.OfType<HeadingBlock>().FirstOrDefault()?.Text;
        return new ParsedContent(blocks) { Title = title, Properties = properties };
    }

    /// <summary>Readability's idea in a few lines: trust semantic elements, else score containers by paragraph text.</summary>
    private static IElement? MainContent(IHtmlDocument document)
    {
        if (document.QuerySelector("main article, article, main, [role=main]") is { } semantic) return semantic;
        return document.Body?.QuerySelectorAll("div, section, td")
            .Where(e => !e.Ancestors<IElement>().Any(a => Boilerplate.Contains(a.LocalName)))
            .OrderByDescending(e => e.Children.Where(c => c.LocalName == "p").Sum(p => p.TextContent.Length) - LinkText(e))
            .FirstOrDefault() ?? document.Body;
    }

    private static int LinkText(IElement e) => e.QuerySelectorAll("a").Sum(a => a.TextContent.Length);

    private static bool IsMain(IElement e) => e.LocalName is "main" or "article";

    private void Walk(IElement element, List<Block> blocks, int listLevel, bool insideMain)
    {
        foreach (var child in element.Children)
        {
            // In full-page mode nothing is labeled: finding the template is the pipeline's job (Chapter 6).
            string? role = null;
            if (_options.MainContentOnly && Boilerplate.Contains(child.LocalName)) continue;
            if (child.LocalName is "script" or "style" or "noscript" or "template") continue;

            switch (child.LocalName)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Add(blocks, new HeadingBlock(child.LocalName[1] - '0', Text(child)) { Role = role });
                    break;
                case "p":
                    Add(blocks, new ParagraphBlock(Text(child)) { Role = role });
                    break;
                case "ul" or "ol":
                    WalkList(child, blocks, listLevel + 1, role);
                    break;
                case "table":
                    var rows = child.QuerySelectorAll("tr").Select(r => (IReadOnlyList<string>)[.. r.Children.Where(c => c.LocalName is "td" or "th").Select(Text)]).ToList();
                    var headerRows = child.QuerySelectorAll("tr").TakeWhile(r => r.Children.All(c => c.LocalName == "th")).Count();
                    Add(blocks, new TableBlock(rows, Math.Max(headerRows, 1)) { Caption = child.QuerySelector("caption") is { } c ? Text(c) : null, Role = role });
                    break;
                case "pre":
                    Add(blocks, new CodeBlock(child.TextContent.TrimEnd()) { Role = role });
                    break;
                case "a" or "span" or "strong" or "em" when child.ParentElement is { LocalName: "div" or "header" or "footer" }:
                    Add(blocks, new ParagraphBlock(Text(child)) { Role = role });
                    break;
                default:
                    // Containers (div, section, header, nav...) are walked; their own loose text becomes a paragraph.
                    var loose = string.Concat(child.ChildNodes.OfType<IText>().Select(t => t.Text));
                    if (!string.IsNullOrWhiteSpace(loose)) Add(blocks, new ParagraphBlock(TextCleanup.Normalize(loose)) { Role = role });
                    Walk(child, blocks, listLevel, insideMain || IsMain(child));
                    break;
            }
        }
    }

    private static void WalkList(IElement list, List<Block> blocks, int level, string? role)
    {
        var number = 1;
        foreach (var item in list.Children.Where(c => c.LocalName == "li"))
        {
            var own = string.Concat(item.ChildNodes.Where(n => n is not IElement e || e.LocalName is not ("ul" or "ol")).Select(n => n.TextContent));
            Add(blocks, new ListItemBlock(TextCleanup.Normalize(own), level, list.LocalName == "ol" ? $"{number++}." : "-") { Role = role });
            foreach (var nested in item.Children.Where(c => c.LocalName is "ul" or "ol"))
            {
                WalkList(nested, blocks, level + 1, role);
            }
        }
    }

    private static void Add(List<Block> blocks, Block block)
    {
        if (block.ToMarkdown().Trim().Length > 0) blocks.Add(block);
    }

    private static string Text(IElement element) => TextCleanup.Normalize(element.TextContent);
}
