using Contoso.Knowledge.Documents;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = Contoso.Knowledge.Documents.Block;
using HeadingBlock = Contoso.Knowledge.Documents.HeadingBlock;
using ParagraphBlock = Contoso.Knowledge.Documents.ParagraphBlock;
using CodeBlock = Contoso.Knowledge.Documents.CodeBlock;
using ListItemBlock = Contoso.Knowledge.Documents.ListItemBlock;

namespace Contoso.Knowledge.Parsing;

/// <summary>Markdown (wiki pages, exported notes) through Markdig's syntax tree, with pipe tables and front matter.</summary>
public sealed class MarkdownParser : IDocumentParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseYamlFrontMatter().Build();

    public string Name => "markdown";

    public string Version => "1.0";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Markdown];

    public async Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content);
        var markdown = await reader.ReadToEndAsync(cancellationToken);
        var syntax = Markdown.Parse(markdown, Pipeline);
        var blocks = new List<Block>();
        Walk(syntax, blocks, level: 1);
        return new ParsedContent(blocks) { Title = blocks.OfType<HeadingBlock>().FirstOrDefault()?.Text };
    }

    private static void Walk(ContainerBlock container, List<Block> blocks, int level)
    {
        foreach (var node in container)
        {
            switch (node)
            {
                case Markdig.Syntax.HeadingBlock h:
                    blocks.Add(new HeadingBlock(h.Level, Inline(h.Inline)));
                    break;
                case Markdig.Syntax.ParagraphBlock p:
                    blocks.Add(new ParagraphBlock(Inline(p.Inline)));
                    break;
                case ListBlock list:
                    WalkList(list, blocks, level);
                    break;
                case Table table:
                    var rows = table.OfType<TableRow>()
                        .Select(r => (IReadOnlyList<string>)[.. r.OfType<TableCell>().Select(c => string.Join(' ', c.OfType<Markdig.Syntax.ParagraphBlock>().Select(p => Inline(p.Inline))))])
                        .ToList();
                    blocks.Add(new TableBlock(rows, HeaderRows: table.OfType<TableRow>().Count(r => r.IsHeader)));
                    break;
                case FencedCodeBlock code:
                    blocks.Add(new CodeBlock(code.Lines.ToString(), code.Info));
                    break;
                case QuoteBlock quote:
                    Walk(quote, blocks, level);
                    break;
            }
        }
    }

    private static void WalkList(ListBlock list, List<Block> blocks, int level)
    {
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<Markdig.Syntax.ListItemBlock>())
        {
            var marker = list.IsOrdered ? $"{number++}." : "-";
            var text = string.Join(' ', item.OfType<Markdig.Syntax.ParagraphBlock>().Select(p => Inline(p.Inline)));
            blocks.Add(new ListItemBlock(text, level, marker));
            foreach (var nested in item.OfType<ListBlock>())
            {
                WalkList(nested, blocks, level + 1);
            }
        }
    }

    private static string Inline(ContainerInline? inline) => inline is null ? "" : TextCleanup.Normalize(InlineText(inline));

    private static string InlineText(Inline inline) => inline switch
    {
        LiteralInline literal => literal.Content.ToString(),
        CodeInline code => code.Content,
        LineBreakInline => " ",
        ContainerInline container => string.Concat(container.Select(InlineText)),
        _ => "",
    };
}
