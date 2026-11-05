using System.Text.RegularExpressions;
using Contoso.Knowledge.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Contoso.Knowledge.Parsing;

/// <summary>
/// Running headers and footers: lines in the top or bottom band of the page that repeat across pages once digits
/// are ignored ("Page 3 of 12"), or, in a one-page document, small text in those bands.
/// </summary>
internal sealed partial class RunningText
{
    private const double Band = 0.085;
    private readonly Dictionary<int, List<(PdfRectangle Box, string Text, string Role)>> _byPage = [];

    public static RunningText Find(IReadOnlyList<Page> pages)
    {
        var lines = new List<(int Page, PdfRectangle Box, string Text, string Role, string Key, double Size)>();
        foreach (var page in pages.Where(p => p.Letters.Count > 0))
        {
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).Where(w => !string.IsNullOrWhiteSpace(w.Text));
            foreach (var line in words.GroupBy(w => Math.Round(w.BoundingBox.Bottom)))
            {
                var box = PdfParser.Union(line.Select(w => w.BoundingBox));
                var center = (box.Top + box.Bottom) / 2;
                var role = center > page.Height * (1 - Band) ? BlockRoles.Header : center < page.Height * Band ? BlockRoles.Footer : null;
                if (role is null) continue;
                var text = string.Join(' ', line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
                lines.Add((page.Number, box, text, role, $"{role}|{Math.Round(center / 3)}|{Digits().Replace(text, "#")}", line.First().Letters[0].PointSize));
            }
        }

        var pageCount = pages.Count(p => p.Letters.Count > 0);
        var repeated = lines.GroupBy(l => l.Key).Where(g => g.Select(l => l.Page).Distinct().Count() >= Math.Max(2, pageCount / 2)).Select(g => g.Key).ToHashSet();
        var bodySize = pages.SelectMany(p => p.Letters).GroupBy(l => Math.Round(l.PointSize)).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(10);
        var result = new RunningText();
        foreach (var line in lines.Where(l => repeated.Contains(l.Key) || (pageCount == 1 && l.Size < bodySize - 1)))
        {
            if (!result._byPage.TryGetValue(line.Page, out var list)) result._byPage[line.Page] = list = [];
            list.Add((line.Box, line.Text, line.Role));
        }

        return result;
    }

    public IEnumerable<(PdfRectangle Box, string Text, string Role)> On(Page page, IReadOnlyList<Word> words) =>
        _byPage.GetValueOrDefault(page.Number) ?? [];

    public bool Covers(Page page, Word word) =>
        _byPage.GetValueOrDefault(page.Number)?.Any(r => Contains(r.Box, word.BoundingBox.Centroid)) ?? false;

    internal static bool Contains(PdfRectangle box, PdfPoint point) =>
        point.X >= box.Left - 0.5 && point.X <= box.Right + 0.5 && point.Y >= box.Bottom - 0.5 && point.Y <= box.Top + 0.5;

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}

/// <summary>
/// Tables drawn with ruling lines: stroked segments are clustered into grids, the grid's rows and columns become
/// cells, and words are assigned to cells by their centers. Leading rows of bold text are header rows, and a bold
/// line no larger than the body text just above the grid is its caption.
/// </summary>
internal static class RuledTables
{
    public static IEnumerable<(PdfRectangle Box, TableBlock Block, IReadOnlyList<Word> Words)> Find(Page page, IReadOnlyList<Word> words, double bodySize)
    {
        var segments = page.Paths.Where(p => p.IsStroked)
            .Select(p => p.GetBoundingRectangle()).OfType<PdfRectangle>()
            .Where(r => (r.Height < 1.5 && r.Width > 4) || (r.Width < 1.5 && r.Height > 4))
            .ToList();

        foreach (var cluster in Clusters(segments))
        {
            var ys = Distinct(cluster.Where(r => r.Height < 1.5).Select(r => r.Bottom)).OrderDescending().ToList();
            var xs = Distinct(cluster.Where(r => r.Width < 1.5).Select(r => r.Left)).Order().ToList();
            if (ys.Count < 2 || xs.Count < 2) continue;

            var box = new PdfRectangle(xs[0], ys[^1], xs[^1], ys[0]);
            var inside = words.Where(w => RunningText.Contains(box, w.BoundingBox.Centroid)).ToList();
            var rows = new List<IReadOnlyList<string>>();
            var bold = new List<bool>();
            for (var r = 0; r < ys.Count - 1; r++)
            {
                var cells = new List<string>();
                var rowWords = new List<Word>();
                for (var c = 0; c < xs.Count - 1; c++)
                {
                    var cell = new PdfRectangle(xs[c], ys[r + 1], xs[c + 1], ys[r]);
                    var cellWords = inside.Where(w => RunningText.Contains(cell, w.BoundingBox.Centroid))
                        .OrderByDescending(w => Math.Round(w.BoundingBox.Bottom)).ThenBy(w => w.BoundingBox.Left).ToList();
                    rowWords.AddRange(cellWords);
                    cells.Add(TextCleanup.Normalize(TextCleanup.JoinLines(cellWords.GroupBy(w => Math.Round(w.BoundingBox.Bottom))
                        .Select(line => string.Join(' ', line.Select(w => w.Text))))));
                }

                rows.Add(cells);
                bold.Add(rowWords.Count > 0 && rowWords.All(w => w.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true));
            }

            var used = new List<Word>(inside);
            string? caption = null;
            var captionWords = words.Where(w => w.BoundingBox.Bottom > box.Top && w.BoundingBox.Bottom < box.Top + 18 &&
                w.BoundingBox.Left >= box.Left - 2 && w.BoundingBox.Right <= box.Right + 2).OrderBy(w => w.BoundingBox.Left).ToList();
            if (captionWords.Count > 0 && captionWords.All(w => w.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true && w.Letters[0].PointSize <= bodySize + 0.1))
            {
                caption = string.Join(' ', captionWords.Select(w => w.Text));
                used.AddRange(captionWords);
                box = PdfParser.Union([box, .. captionWords.Select(w => w.BoundingBox)]);
            }

            yield return (box, new TableBlock(rows, Math.Max(1, bold.TakeWhile(b => b).Count())) { Caption = caption }, used);
        }
    }

    /// <summary>Groups segments that touch, so that each group is one table's grid.</summary>
    private static IEnumerable<List<PdfRectangle>> Clusters(List<PdfRectangle> segments)
    {
        var parent = Enumerable.Range(0, segments.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = i + 1; j < segments.Count; j++)
            {
                if (Touch(segments[i], segments[j])) parent[Root(i)] = Root(j);
            }
        }

        return segments.Select((s, i) => (s, Root(i))).GroupBy(x => x.Item2).Select(g => g.Select(x => x.s).ToList());
    }

    private static bool Touch(PdfRectangle a, PdfRectangle b) =>
        a.Left <= b.Right + 2 && b.Left <= a.Right + 2 && a.Bottom <= b.Top + 2 && b.Bottom <= a.Top + 2;

    private static IEnumerable<double> Distinct(IEnumerable<double> values)
    {
        double? last = null;
        foreach (var v in values.Order())
        {
            if (last is null || v - last > 1.5)
            {
                yield return v;
                last = v;
            }
        }
    }
}

/// <summary>Figures: a framed region with small labels inside, and an italic "Figure..." caption just below the frame.</summary>
internal static class Figures
{
    public static IEnumerable<(PdfRectangle Box, FigureBlock Block, IReadOnlyList<Word> Words)> Find(Page page, IReadOnlyList<Word> words)
    {
        var frames = page.Paths.Where(p => p.IsStroked && !p.IsFilled)
            .Select(p => p.GetBoundingRectangle()).OfType<PdfRectangle>()
            .Where(r => r.Width > 120 && r.Height > 60)
            .ToList();
        foreach (var frame in frames.Where(f => !frames.Any(o => o != f && o.Width > f.Width && RunningText.Contains(o, f.Centroid))))
        {
            var inside = words.Where(w => RunningText.Contains(frame, w.BoundingBox.Centroid)).ToList();
            var labels = new List<string>();
            foreach (var line in inside.GroupBy(w => Math.Round(w.BoundingBox.Bottom)).OrderByDescending(g => g.Key))
            {
                // Labels on one line are separate when the gap between words is wide or a callout number starts a new one.
                var current = new List<Word>();
                foreach (var word in line.OrderBy(w => w.BoundingBox.Left))
                {
                    var callout = int.TryParse(word.Text, out _) && current.Count > 0;
                    if (current.Count > 0 && (word.BoundingBox.Left - current[^1].BoundingBox.Right > 8 || callout))
                    {
                        labels.Add(string.Join(' ', current.Select(w => w.Text)));
                        current.Clear();
                    }

                    current.Add(word);
                }

                if (current.Count > 0) labels.Add(string.Join(' ', current.Select(w => w.Text)));
            }

            var captionWords = words.Where(w => w.BoundingBox.Top < frame.Bottom && w.BoundingBox.Top > frame.Bottom - 22 &&
                w.BoundingBox.Left >= frame.Left - 2 && w.BoundingBox.Right <= frame.Right + 2).OrderBy(w => w.BoundingBox.Left).ToList();
            var caption = captionWords.Count > 0 && captionWords[0].Text == "Figure" ? string.Join(' ', captionWords.Select(w => w.Text)) : null;
            var used = caption is null ? inside : [.. inside, .. captionWords];
            var box = caption is null ? frame : PdfParser.Union([frame, .. captionWords.Select(w => w.BoundingBox)]);
            yield return (box, new FigureBlock(caption) { Labels = OrderLabels(labels) }, used);
        }
    }

    // Numbered labels ("1 Housing") read in number order, which is how a reader follows the callouts.
    private static IReadOnlyList<string> OrderLabels(List<string> labels) =>
        labels.All(l => char.IsDigit(l[0])) ? [.. labels.OrderBy(l => int.Parse(new string([.. l.TakeWhile(char.IsDigit)])))] : labels;
}

/// <summary>
/// Reading order from positions: running headers first and footers last; on a two-column page, full-width regions
/// split the page into bands, and within a band the left column is read before the right.
/// </summary>
internal static class ReadingOrder
{
    public static IEnumerable<(PdfRectangle Box, Block Block)> Sort(List<(PdfRectangle Box, Block Block)> regions, double pageWidth)
    {
        var mid = pageWidth / 2;
        int Column(PdfRectangle b) => b.Right <= mid + 8 ? 0 : b.Left >= mid - 8 ? 1 : -1;
        var body = regions.Where(r => r.Block.Role is null).ToList();
        var twoColumns = body.Any(r => Column(r.Box) == 1) && body.Any(r => Column(r.Box) == 0);
        var full = body.Where(r => !twoColumns || Column(r.Box) == -1).ToList();

        (int, int, double) Key((PdfRectangle Box, Block Block) r)
        {
            if (r.Block.Role == BlockRoles.Header) return (-1, 0, -r.Box.Top);
            if (r.Block.Role is not null) return (int.MaxValue, 0, -r.Box.Top);
            if (!twoColumns) return (0, 0, -r.Box.Top);
            var above = full.Count(f => f.Box.Top > r.Box.Top + 1);
            var column = Column(r.Box);
            return column == -1 ? (2 * above + 2, 0, -r.Box.Top) : (2 * above + 1, column, -r.Box.Top);
        }

        return regions.OrderBy(Key);
    }
}

/// <summary>Joins what page and column breaks split: paragraphs and list items that continue, and tables that repeat their header.</summary>
public static class Continuations
{
    public static List<Block> Merge(IEnumerable<IEnumerable<Block>> pages)
    {
        var result = new List<Block>();
        foreach (var page in pages)
        {
            var firstOnPage = true;
            foreach (var block in page)
            {
                var lastIndex = result.FindLastIndex(b => b.Role is null);
                var last = lastIndex >= 0 ? result[lastIndex] : null;
                if (block.Role is null && last is not null && Continue(last, block, firstOnPage) is { } merged)
                {
                    result[lastIndex] = merged;
                }
                else
                {
                    result.Add(block);
                }

                if (block.Role is null) firstOnPage = false;
            }
        }

        return result;
    }

    private static Block? Continue(Block last, Block next, bool firstOnPage) => (last, next) switch
    {
        (ParagraphBlock p, ParagraphBlock q) when Continues(p.Text, q.Text) => p with { Text = TextCleanup.JoinLines([p.Text, q.Text]) },
        (ListItemBlock l, ParagraphBlock q) when Continues(l.Text, q.Text) => l with { Text = TextCleanup.JoinLines([l.Text, q.Text]) },
        (TableBlock a, TableBlock b) when firstOnPage && SameHeader(a, b) => a with { Rows = [.. a.Rows, .. b.Rows.Skip(b.HeaderRows)] },
        (HeadingBlock a, HeadingBlock b) when a.Level == b.Level && Stacked(a, b) => a with { Text = $"{a.Text} {b.Text}" },
        _ => null,
    };

    // A heading set on two lines comes out of the segmenter as two headings, one right under the other.
    private static bool Stacked(Block a, Block b) =>
        a.Anchor?.Page == b.Anchor?.Page && a.Anchor?.Box is { } upper && b.Anchor?.Box is { } lower &&
        lower.Top - upper.Bottom < upper.Height && Math.Abs(lower.Left - upper.Left) < 2;

    private static bool Continues(string previous, string next) =>
        previous.Length > 0 && next.Length > 0 && ".:;?!".IndexOf(previous[^1]) < 0 && (char.IsLower(next[0]) || previous[^1] == '-');

    // A table that opens a page with the same header row as the table that closed the previous one continues it.
    private static bool SameHeader(TableBlock a, TableBlock b) =>
        b.Caption is null && b.HeaderRows > 0 && a.Rows.Count > 0 && b.Rows.Count > 0 &&
        a.Rows[0].Count == b.Rows[0].Count && a.Rows[0].SequenceEqual(b.Rows[0]);
}
