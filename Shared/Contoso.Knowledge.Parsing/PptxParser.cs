using Contoso.Knowledge.Documents;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using Block = Contoso.Knowledge.Documents.Block;

namespace Contoso.Knowledge.Parsing;

/// <summary>
/// PowerPoint decks through the Open XML SDK, slide by slide in presentation order: the title placeholder becomes
/// a heading, other text a list, and the speaker notes a paragraph with the speaker-notes role, where decks often
/// keep their most useful sentences.
/// </summary>
public sealed class PptxParser : IDocumentParser
{
    public string Name => "pptx";

    public string Version => "1.0";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Pptx];

    public Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        using var deck = PresentationDocument.Open(content, false);
        var presentation = deck.PresentationPart ?? throw new InvalidDataException($"{context.FileName} has no presentation part.");
        var blocks = new List<Block>();
        var number = 0;
        foreach (var id in presentation.Presentation?.SlideIdList?.Elements<SlideId>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            var slide = (SlidePart)presentation.GetPartById(id.RelationshipId!.Value!);
            var anchor = new SourceAnchor { Path = $"slide {number}" };
            foreach (var shape in slide.Slide?.Descendants<Shape>() ?? [])
            {
                var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<PlaceholderShape>();
                var paragraphs = Paragraphs(shape);
                if (placeholder?.Type?.Value is { } type && (type == PlaceholderValues.Title || type == PlaceholderValues.CenteredTitle))
                {
                    blocks.Add(new HeadingBlock(number == 1 ? 1 : 2, string.Join(' ', paragraphs)) { Anchor = anchor });
                }
                else
                {
                    blocks.AddRange(paragraphs.Select(p => new ListItemBlock(p) { Anchor = anchor }));
                }
            }

            var notes = slide.NotesSlidePart?.NotesSlide?.Descendants<Shape>()
                .Where(s => s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<PlaceholderShape>()?.Type?.Value == PlaceholderValues.Body)
                .SelectMany(Paragraphs).ToList();
            if (notes is { Count: > 0 })
            {
                blocks.Add(new ParagraphBlock(string.Join(' ', notes)) { Role = BlockRoles.SpeakerNotes, Anchor = new SourceAnchor { Path = $"slide {number}/notes" } });
            }
        }

        return Task.FromResult(new ParsedContent(blocks) { Title = deck.PackageProperties.Title });
    }

    private static List<string> Paragraphs(Shape shape) =>
        [.. shape.TextBody?.Elements<A.Paragraph>()
            .Select(p => TextCleanup.Normalize(string.Concat(p.Descendants<A.Text>().Select(t => t.Text))))
            .Where(t => t.Length > 0) ?? []];
}
