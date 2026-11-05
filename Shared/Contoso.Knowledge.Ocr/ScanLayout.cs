using System.Text.RegularExpressions;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Parsing;

namespace Contoso.Knowledge.Ocr;

/// <summary>What one scanned page became: blocks in reading order and what a person should check.</summary>
public sealed record ScanPage(int Number, IReadOnlyList<Block> Blocks, IReadOnlyList<ReviewItem> Review, double SkewDegrees, int CellsReread, int Corrections);

/// <summary>Something on a page a person should check: a line or cell read with low confidence, or a handwritten mark.</summary>
public sealed record ReviewItem(int Page, BoundingBox Box, string Text, double Confidence, string Reason);

/// <summary>
/// Layout for a scanned page: running text by position, tables from ruling lines, figures from non-text ink,
/// then a recursive XY-cut over everything else for reading order, and headings, list items and paragraphs
/// from line height, list markers and spacing. Every block carries its page, its box in page points and the
/// engine's confidence.
/// </summary>
public static partial class ScanLayout
{
    public static ScanPage Read(int number, PreparedPage page, IOcrEngine engine, double reviewBelow = 0.8, bool postCorrect = true)
    {
        var height = page.Image.Height;
        var ocr = engine.Recognize(page.Image);
        var grids = ScanTables.Find(page.Image);
        var corrections = 0;
        var lines = Rows(ocr.Lines).Select(l =>
        {
            if (!postCorrect) return l;
            var text = OcrText.Correct(l.Text, out var n);
            corrections += n;
            return l with { Text = text };
        }).ToList();
        bool InGrid(OcrLine l) => grids.Any(g => g.Box.Contains(l.Box.CenterX, l.Box.CenterY));
        var header = lines.Where(l => l.Box.Bottom < 0.085 * height).ToList();
        var footer = lines.Where(l => l.Box.Top > 0.915 * height).ToList();
        var body = lines.Except(header).Except(footer).Where(l => !InGrid(l)).ToList();
        var figures = ScanFigures.Find(page.Image, body.Select(l => l.Box), grids.Select(g => g.Box));
        var bodyHeight = body.Count == 0 ? 30 : Median(body.Select(l => l.Box.Height));
        var bodyWidth = body.Count == 0 ? 15 : Median(body.Select(CharWidth));

        // Line pitch, top to top, is steadier than the gap between boxes: engines pad their boxes differently.
        var ordered = body.OrderBy(l => l.Box.Top).ToList();
        var steps = ordered.Zip(ordered.Skip(1), (a, b) => b.Box.Top - a.Box.Top).Where(d => d > 0.5 * bodyHeight && d < 2.5 * bodyHeight).ToList();
        var pitch = steps.Count == 0 ? 1.4 * bodyHeight : Median(steps);

        SourceAnchor Anchor(PixelBox box, double? confidence) => new() { Page = number, Box = page.ToPagePoints(box), Confidence = confidence is { } c ? Math.Round(c, 3) : null };
        var review = new List<ReviewItem>();
        var reread = 0;

        // Every region becomes one item for the XY-cut: a line, a table or a figure with its caption and labels.
        var items = new List<Item>();
        var used = new HashSet<OcrLine>();
        foreach (var grid in grids)
        {
            var table = ScanTables.Read(grid, [.. ocr.Lines.SelectMany(l => l.Words)], page.Image, engine, reviewBelow, postCorrect);
            reread += table.Reread;
            // The caption is the line right above the grid, if it reads like one.
            var caption = body.Where(l => !used.Contains(l) && l.Box.CenterY < grid.Box.Top && grid.Box.Top - l.Box.CenterY < 2 * pitch)
                .OrderByDescending(l => l.Box.Bottom).FirstOrDefault() is { } above && IsCaption(above, "Table") ? above : null;
            if (caption is not null) used.Add(caption);
            var box = caption is null ? grid.Box : PixelBox.Union([grid.Box, caption.Box]);
            var cells = Enumerable.Range(0, grid.RowCount).SelectMany(r => Enumerable.Range(0, grid.ColumnCount).Select(c => table.Confidence[r, c])).ToList();
            items.Add(new Item(box, table.Block with { Caption = caption?.Text, Anchor = Anchor(box, cells.Average()) }));
            for (var r = 0; r < grid.RowCount; r++)
            {
                for (var c = 0; c < grid.ColumnCount; c++)
                {
                    if (table.Confidence[r, c] < reviewBelow)
                    {
                        review.Add(new ReviewItem(number, page.ToPagePoints(grid.Cell(r, c)), table.Block.Rows[r][c], table.Confidence[r, c], "table cell"));
                    }
                }
            }
        }

        foreach (var figure in figures)
        {
            used.UnionWith(body.Where(l => figure.Contains(l.Box.CenterX, l.Box.CenterY)));

            // Labels are the engine's own lines inside the drawing: rows joined across a drawing would merge labels.
            var labels = ocr.Lines.Where(l => figure.Contains(l.Box.CenterX, l.Box.CenterY) && l.Confidence >= 0.5 && l.Text.Count(char.IsLetterOrDigit) >= 2)
                .Select(l => l with { Text = OcrText.Correct(l.Text) }).ToList();
            var caption = body.Where(l => !used.Contains(l) && l.Box.CenterY > figure.Bottom && l.Box.CenterY - figure.Bottom < 2 * pitch)
                .OrderBy(l => l.Box.Top).FirstOrDefault() is { } below && IsCaption(below, "Figure") ? below : null;
            if (caption is not null) used.Add(caption);
            var box = PixelBox.Union(labels.Select(l => l.Box).Append(figure).Concat(caption is null ? [] : [caption.Box]));
            var block = new FigureBlock(caption?.Text) { Labels = [.. labels.OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left).Select(l => l.Text)] };
            items.Add(new Item(box, block with { Anchor = Anchor(box, labels.Count == 0 ? null : labels.Average(l => l.Confidence)) }));
        }

        items.AddRange(body.Where(l => !used.Contains(l)).Select(l => new Item(l.Box, Line: l)));

        var blocks = new List<Block>();
        if (header.Count > 0) blocks.Add(Running(header, BlockRoles.Header, Anchor));
        foreach (var leaf in XyCut(items, 0.7 * bodyHeight, 2.0 * bodyHeight))
        {
            blocks.AddRange(Segment(leaf, l => IsHeading(l, bodyHeight, bodyWidth), pitch, Anchor));
        }

        // Stamps, signatures and pen notes, read from the colored ink dropped before OCR. Each follows the body
        // block it overlaps most on the page, so a note written beside a heading stays with that section.
        foreach (var mark in Marks.Read(page, engine))
        {
            var block = new ParagraphBlock(mark.Text) { Role = BlockRoles.Annotation, Anchor = Anchor(mark.Box, mark.Confidence) };
            var box = block.Anchor!.Box!.Value;
            var host = blocks.Select((b, i) => (Index: i, Overlap: b.Role is null && b.Anchor?.Box is { } a ? Math.Min(a.Bottom, box.Bottom) - Math.Max(a.Top, box.Top) : 0))
                .Where(h => h.Overlap > 0).OrderByDescending(h => h.Overlap).Select(h => (int?)h.Index).FirstOrDefault();
            var at = host is { } i ? i + 1 : blocks.Count(b => b.Anchor?.Box is { } a && a.Top < box.Top);
            blocks.Insert(Math.Min(at, blocks.Count), block);
            if (mark.Confidence < reviewBelow) review.Add(new ReviewItem(number, box, mark.Text, mark.Confidence, "mark on paper"));
        }

        if (footer.Count > 0) blocks.Add(Running(footer, BlockRoles.Footer, Anchor));

        review.AddRange(body.Where(l => !used.Contains(l) && l.Confidence < reviewBelow)
            .Select(l => new ReviewItem(number, page.ToPagePoints(l.Box), l.Text, l.Confidence, "low-confidence line")));
        return new ScanPage(number, blocks, review, page.SkewDegrees, reread, corrections);
    }

    private sealed record Item(PixelBox Box, Block? Block = null, OcrLine? Line = null);

    /// <summary>
    /// Recursive XY-cut: split at the widest horizontal gap between bands of items, or at the widest vertical gap
    /// between columns when no horizontal gap is wide enough, and recurse. Leaves come out in reading order.
    /// </summary>
    private static List<List<Item>> XyCut(List<Item> items, double minGapY, double minGapX)
    {
        if (items.Count <= 1) return items.Count == 0 ? [] : [items];
        var (gapY, atY) = WidestGap(items.Select(i => (i.Box.Top, i.Box.Bottom)));
        var (gapX, atX) = WidestGap(items.Select(i => (i.Box.Left, i.Box.Right)));
        if (gapY >= minGapY && (gapY >= gapX || gapX < minGapX))
        {
            return [.. XyCut([.. items.Where(i => i.Box.CenterY < atY)], minGapY, minGapX), .. XyCut([.. items.Where(i => i.Box.CenterY >= atY)], minGapY, minGapX)];
        }

        if (gapX >= minGapX)
        {
            return [.. XyCut([.. items.Where(i => i.Box.CenterX < atX)], minGapY, minGapX), .. XyCut([.. items.Where(i => i.Box.CenterX >= atX)], minGapY, minGapX)];
        }

        return [items];
    }

    private static (double Gap, double At) WidestGap(IEnumerable<(double Start, double End)> spans)
    {
        var sorted = spans.OrderBy(s => s.Start).ToList();
        double best = 0, at = 0, reach = sorted[0].End;
        foreach (var (start, end) in sorted.Skip(1))
        {
            if (start - reach > best) (best, at) = (start - reach, (start + reach) / 2);
            reach = Math.Max(reach, end);
        }

        return (best, at);
    }

    /// <summary>Headings by height and numbering, list items by their markers, paragraphs from the rest.</summary>
    private static IEnumerable<Block> Segment(List<Item> leaf, Func<OcrLine, bool> isHeading, double pitch, Func<PixelBox, double?, SourceAnchor> anchor)
    {
        var pending = new List<OcrLine>();
        Block? Flush()
        {
            if (pending.Count == 0) return null;
            var lines = pending.ToList();
            pending.Clear();
            var text = TextCleanup.JoinLines(lines.Select(l => l.Text));
            var a = anchor(PixelBox.Union(lines.Select(l => l.Box)), lines.Average(l => l.Confidence));
            if (isHeading(lines[0])) return new HeadingBlock(Level(text), text) { Anchor = a };
            if (ListMarker().Match(text) is { Success: true } m) return new ListItemBlock(m.Groups["text"].Value, 1, Number(m.Groups["marker"].Value)) { Anchor = a };
            return new ParagraphBlock(text) { Anchor = a };
        }

        // Lines and marks by their middles, so a note written beside a heading follows it; tables and figures by their tops.
        foreach (var item in leaf.OrderBy(i => i.Block is TableBlock or FigureBlock ? i.Box.Top : i.Box.CenterY))
        {
            if (item.Block is not null)
            {
                if (Flush() is { } b) yield return b;
                yield return item.Block;
                continue;
            }

            var line = item.Line!;
            var startsBlock = pending.Count > 0 && (
                isHeading(line) != isHeading(pending[0]) ||
                ListMarker().IsMatch(line.Text) ||
                line.Box.Top - pending[^1].Box.Top > 1.3 * pitch);
            if (startsBlock && Flush() is { } block) yield return block;
            pending.Add(line);
        }

        if (Flush() is { } last) yield return last;
    }

    private static Block Running(List<OcrLine> lines, string role, Func<PixelBox, double?, SourceAnchor> anchor) =>
        new ParagraphBlock(string.Join(' ', lines.OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left).Select(l => l.Text)))
        {
            Role = role,
            Anchor = anchor(PixelBox.Union(lines.Select(l => l.Box)), lines.Average(l => l.Confidence)),
        };

    /// <summary>Lines the engine split on one baseline (PaddleOCR splits at wide gaps) are joined left to right.</summary>
    internal static List<OcrLine> Rows(IEnumerable<OcrLine> lines, double tolerance = 0.4)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var line in lines.OrderBy(l => l.Box.CenterY).ThenBy(l => l.Box.Left))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].Box.CenterY - line.Box.CenterY) < tolerance * Math.Min(r[0].Box.Height, line.Box.Height)
                && r.All(o => o.Box.Right < line.Box.Left || o.Box.Left > line.Box.Right) && Math.Abs(r[0].Box.Height - line.Box.Height) < 0.3 * line.Box.Height);
            if (row is null) rows.Add([line]);
            else row.Add(line);
        }

        return [.. rows.Select(r => r.Count == 1 ? r[0] : TesseractEngine.Line([.. r.OrderBy(l => l.Box.Left).SelectMany(l => l.Words)]))];
    }

    /// <summary>
    /// Larger type: taller lines, or wider characters. Width matters because a heading without descenders, such as
    /// "4 Installation", is no taller than a body line with them.
    /// </summary>
    private static bool IsHeading(OcrLine line, double bodyHeight, double bodyWidth) =>
        !line.Text.EndsWith('.') && line.Text.Split(' ').Length <= 14 &&
        (line.Box.Height >= 1.2 * bodyHeight || CharWidth(line) >= 1.2 * bodyWidth || (NumberedHeading().IsMatch(line.Text) && line.Text.Split(' ').Length <= 8));

    private static double CharWidth(OcrLine line) => line.Words.Sum(w => w.Box.Width) / Math.Max(1, line.Words.Sum(w => w.Text.Length));

    /// <summary>"Table 3-1 Technical data", "Figure 8-1 Seal housing": short, numbered, no full stop. A short unnumbered line counts above a table.</summary>
    private static bool IsCaption(OcrLine line, string kind)
    {
        var words = line.Text.Split(' ').Length;
        if (line.Text.EndsWith('.')) return false;
        return line.Text.StartsWith(kind + " ", StringComparison.Ordinal) ? words <= 10 : kind == "Table" && words <= 6;
    }

    /// <summary>"6.3 Flange bolt torque" is a level-2 heading; unnumbered headings are level 1.</summary>
    private static int Level(string heading) => SectionNumber().Match(heading) is { Success: true } m ? m.Value.Count(c => c == '.') + 1 : 1;

    private static string? Number(string marker) => char.IsDigit(marker[0]) ? marker : null;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    // Bullets as engines read them: •, ·, -, +, *, », or a dot stuck to the first word; numbers such as "3." or "b)".
    [GeneratedRegex(@"^(?<marker>[•·▪*+»\-–](?=\s*\S)|\d{1,2}[.)](?=\s)|[a-z][.)](?=\s)|\.(?=[A-Z]))\s*(?<text>\S.*)$")]
    private static partial Regex ListMarker();

    /// <summary>"4 Installation", "7.2 Service intervals": a section number, then a capitalized title.</summary>
    [GeneratedRegex(@"^\d+(\.\d+)*\s+\p{Lu}")]
    private static partial Regex NumberedHeading();

    [GeneratedRegex(@"^\d+(\.\d+)*(?=\s)")]
    private static partial Regex SectionNumber();
}
