using Contoso.Knowledge.Documents;
using SkiaSharp;

namespace Contoso.Corpus.Generator.Writers;

/// <summary>How a paged document looks, including the habits of the software that "produced" it.</summary>
internal sealed record PageStyle
{
    public static PageStyle Default { get; } = new();

    public double Width { get; init; } = 595.28;   // A4 in points

    public double Height { get; init; } = 841.89;

    public double MarginX { get; init; } = 56;

    public double MarginTop { get; init; } = 66;

    public double MarginBottom { get; init; } = 62;

    public int Columns { get; init; } = 1;

    public double Gutter { get; init; } = 22;

    public double BodySize { get; init; } = 10;

    /// <summary>Text printed at the top of every page, such as the manual's title.</summary>
    public string? RunningHeader { get; init; }

    /// <summary>Printed at the bottom of every page; <c>{page}</c> and <c>{pages}</c> are replaced.</summary>
    public string? RunningFooter { get; init; }

    /// <summary>Split long words at line ends with a hyphen, as typesetting software does.</summary>
    public bool Hyphenate { get; init; }

    /// <summary>Write text line by line across both columns, as some PDF producers do, so content order isn't reading order.</summary>
    public bool RowMajorContent { get; init; }

    /// <summary>Write every word as its own positioned text object instead of one per line.</summary>
    public bool WordFragments { get; init; }

    /// <summary>Draw the cell borders of tables.</summary>
    public bool RuledTables { get; init; } = true;

    /// <summary>Indexes of blocks that start a new page, such as a signature page.</summary>
    public IReadOnlySet<int> PageBreaksBefore { get; init; } = new HashSet<int>();
}

internal enum FontKind { Regular, Bold, Italic, Mono, Script }

internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Black { get; } = new(0, 0, 0);

    public static Rgb Gray { get; } = new(90, 90, 90);

    public static Rgb Light { get; } = new(225, 225, 225);

    public static Rgb Red { get; } = new(190, 30, 40);

    public static Rgb Blue { get; } = new(30, 60, 150);
}

/// <summary>A drawing operation in points, origin at the top left of the page.</summary>
internal abstract record DrawOp;

/// <summary>Text whose baseline starts at (<see cref="X"/>, <see cref="Baseline"/>).</summary>
internal sealed record TextOp(double X, double Baseline, string Text, FontKind Font, double Size, Rgb Color) : DrawOp;

internal sealed record LineOp(double X1, double Y1, double X2, double Y2, double Width, Rgb Color) : DrawOp;

internal sealed record RectOp(double X, double Y, double W, double H, double LineWidth, Rgb Color, bool Fill) : DrawOp;

internal sealed record EllipseOp(double CenterX, double CenterY, double Diameter, double LineWidth, Rgb Color) : DrawOp;

internal sealed class LaidOutPage(int number)
{
    public int Number { get; } = number;

    public List<DrawOp> Ops { get; } = [];
}

/// <summary>Text widths from fonts metric-compatible with the PDF standard fonts (Arial for Helvetica, Courier New for Courier).</summary>
internal static class Metrics
{
    private static readonly Dictionary<FontKind, SKTypeface> Faces = new()
    {
        [FontKind.Regular] = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default,
        [FontKind.Bold] = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default,
        [FontKind.Italic] = SKTypeface.FromFamilyName("Arial", SKFontStyle.Italic) ?? SKTypeface.Default,
        [FontKind.Mono] = SKTypeface.FromFamilyName("Courier New") ?? SKTypeface.Default,
        [FontKind.Script] = SKTypeface.FromFamilyName("Segoe Script") ?? SKTypeface.FromFamilyName("Ink Free") ?? SKTypeface.Default,
    };

    public static SKTypeface Face(FontKind font) => Faces[font];

    public static double Width(string text, FontKind font, double size)
    {
        using var skFont = new SKFont(Faces[font], (float)size);
        return skFont.MeasureText(text);
    }
}

/// <summary>
/// Lays canonical blocks out on pages: columns, wrapped and optionally hyphenated lines, ruled tables that
/// repeat their header rows across pages, simple figures, running headers and footers. Records where each
/// block landed, which becomes the truth's anchors.
/// </summary>
internal sealed class PageLayout(PageStyle style)
{
    private readonly List<LaidOutPage> _pages = [];
    private int _column;
    private double _y;
    private LaidOutPage _page = null!;

    private double ColumnWidth => (style.Width - 2 * style.MarginX - (style.Columns - 1) * style.Gutter) / style.Columns;

    private double ColumnX => style.MarginX + _column * (ColumnWidth + style.Gutter);

    private double Bottom => style.Height - style.MarginBottom;

    private double LineHeight(double size) => size * 1.32;

    public (IReadOnlyList<LaidOutPage> Pages, IReadOnlyList<Block> Anchored) Run(IReadOnlyList<Block> blocks)
    {
        NewPage();
        var anchored = new List<Block>(blocks.Count);
        var firstHeading = true;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (style.PageBreaksBefore.Contains(i) && _y > style.MarginTop)
            {
                NewPage();
            }

            var startPage = _page.Number;
            var startTop = _y;
            var startX = ColumnX;
            switch (block)
            {
                case HeadingBlock h:
                    var size = firstHeading && h.Level == 1 ? 18 : h.Level switch { 1 => 15, 2 => 12.5, _ => 11 };
                    firstHeading = false;
                    KeepTogether(LineHeight(size) * 2 + style.BodySize * 3);
                    _y += h.Level == 1 ? 8 : 6;
                    startPage = _page.Number;
                    startTop = _y;
                    startX = ColumnX;
                    WriteWrapped(h.Text, FontKind.Bold, size, 0, ColumnWidth, hyphenate: false);
                    _y += 3;
                    break;
                case ParagraphBlock p:
                    WriteWrapped(p.Text, FontKind.Regular, style.BodySize, 0, ColumnWidth, style.Hyphenate);
                    _y += style.BodySize * 0.7;
                    break;
                case ListItemBlock item:
                    var indent = 18.0 * item.Level;
                    KeepTogether(LineHeight(style.BodySize));
                    _page.Ops.Add(new TextOp(ColumnX + indent - 16, _y + style.BodySize, item.Marker == "-" ? "•" : item.Marker ?? "•",
                        FontKind.Regular, style.BodySize, Rgb.Black));
                    WriteWrapped(item.Text, FontKind.Regular, style.BodySize, indent, ColumnWidth - indent, style.Hyphenate);
                    _y += style.BodySize * 0.3;
                    break;
                case TableBlock table:
                    WriteTable(table);
                    break;
                case FigureBlock figure:
                    WriteFigure(figure);
                    break;
                case CodeBlock code:
                    foreach (var line in code.Text.Split('\n'))
                    {
                        KeepTogether(LineHeight(9));
                        _page.Ops.Add(new TextOp(ColumnX + 6, _y + 9, line, FontKind.Mono, 9, Rgb.Black));
                        _y += LineHeight(9);
                    }

                    _y += 6;
                    break;
            }

            var box = _page.Number == startPage
                ? new BoundingBox(startX, startTop, startX + ColumnWidth, _y)
                : new BoundingBox(startX, startTop, startX + ColumnWidth, Bottom);
            anchored.Add(block with { Anchor = new SourceAnchor { Page = startPage, Box = Round(box) } });
        }

        AddRunningText();
        return (_pages, anchored);
    }

    private static BoundingBox Round(BoundingBox b) =>
        new(Math.Round(b.Left, 1), Math.Round(b.Top, 1), Math.Round(b.Right, 1), Math.Round(b.Bottom, 1));

    private void NewPage()
    {
        _page = new LaidOutPage(_pages.Count + 1);
        _pages.Add(_page);
        _column = 0;
        _y = style.MarginTop;
    }

    private void NextColumn()
    {
        if (_column + 1 < style.Columns)
        {
            _column++;
            _y = style.MarginTop;
        }
        else
        {
            NewPage();
        }
    }

    private void KeepTogether(double height)
    {
        if (_y + height > Bottom)
        {
            NextColumn();
        }
    }

    /// <summary>Writes wrapped lines at <paramref name="indent"/> from the current column's left edge, moving to the next column when full.</summary>
    private void WriteWrapped(string text, FontKind font, double size, double indent, double width, bool hyphenate)
    {
        foreach (var line in Wrap(text, font, size, width, hyphenate))
        {
            KeepTogether(LineHeight(size));
            _page.Ops.Add(new TextOp(ColumnX + indent, _y + size, line, font, size, Rgb.Black));
            _y += LineHeight(size);
        }
    }

    internal static IReadOnlyList<string> Wrap(string text, FontKind font, double size, double width, bool hyphenate)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Metrics.Width(candidate, font, size) <= width)
            {
                current = candidate;
                continue;
            }

            if (hyphenate && Hyphenation(word) is { } splits)
            {
                // Put as much of the word as fits on this line, with a hyphen.
                var placed = false;
                foreach (var split in splits.OrderByDescending(s => s))
                {
                    var head = (current.Length == 0 ? "" : current + " ") + word[..split] + "-";
                    if (Metrics.Width(head, font, size) <= width)
                    {
                        lines.Add(head);
                        current = word[split..];
                        placed = true;
                        break;
                    }
                }

                if (placed) continue;
            }

            if (current.Length > 0) lines.Add(current);
            current = word;
        }

        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    /// <summary>Where a long, plain word may be split: between a vowel and a consonant, three letters in from either end.</summary>
    private static IReadOnlyList<int>? Hyphenation(string word)
    {
        if (word.Length < 9 || !word.All(char.IsLetter)) return null;
        const string vowels = "aeiouy";
        var splits = new List<int>();
        for (var i = 3; i <= word.Length - 3; i++)
        {
            if (vowels.Contains(char.ToLowerInvariant(word[i - 1])) && !vowels.Contains(char.ToLowerInvariant(word[i])))
            {
                splits.Add(i);
            }
        }

        return splits.Count == 0 ? null : splits;
    }

    private void WriteTable(TableBlock table)
    {
        var size = style.BodySize - 1;
        var columns = table.Rows.Max(r => r.Count);
        var width = ColumnWidth;
        var natural = Enumerable.Range(0, columns)
            .Select(c => table.Rows.Max(r => c < r.Count ? Metrics.Width(r[c], c < 1 ? FontKind.Bold : FontKind.Regular, size) : 0) + 10)
            .ToArray();
        var total = natural.Sum();
        var widths = natural.Select(w => Math.Max(42, total <= width ? w * width / total : Math.Min(w, width * 0.6))).ToArray();
        var scale = width / widths.Sum();
        widths = [.. widths.Select(w => w * scale)];

        if (table.Caption is not null)
        {
            KeepTogether(LineHeight(size) * 4);
            WriteWrapped(table.Caption, FontKind.Bold, size, 0, width, hyphenate: false);
            _y += 2;
        }

        var headerRows = table.Rows.Take(Math.Max(table.HeaderRows, 0)).ToList();
        var x0 = ColumnX;
        var needHeader = false;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var height = RowHeight(row, widths, size);
            if (_y + height > Bottom)
            {
                NextColumn();
                x0 = ColumnX;
                needHeader = r >= headerRows.Count;
            }

            if (needHeader)
            {
                // Long tables repeat their header rows on every page, as manuals do (Chapter 9 reads them back).
                foreach (var header in headerRows)
                {
                    DrawRow(header, widths, size, x0, bold: true, RowHeight(header, widths, size));
                }

                needHeader = false;
            }

            DrawRow(row, widths, size, x0, bold: r < headerRows.Count, height);
        }

        _y += style.BodySize;
    }

    private double RowHeight(IReadOnlyList<string> row, double[] widths, double size) =>
        Enumerable.Range(0, widths.Length)
            .Max(c => Wrap(c < row.Count ? row[c] : "", FontKind.Regular, size, widths[c] - 8, false).Count) * LineHeight(size) + 6;

    private void DrawRow(IReadOnlyList<string> row, double[] widths, double size, double x0, bool bold, double height)
    {
        var x = x0;
        if (bold)
        {
            _page.Ops.Add(new RectOp(x0, _y, widths.Sum(), height, 0, Rgb.Light, Fill: true));
        }

        for (var c = 0; c < widths.Length; c++)
        {
            var lineY = _y + 3;
            foreach (var line in Wrap(c < row.Count ? row[c] : "", FontKind.Regular, size, widths[c] - 8, false))
            {
                _page.Ops.Add(new TextOp(x + 4, lineY + size, line, bold ? FontKind.Bold : FontKind.Regular, size, Rgb.Black));
                lineY += LineHeight(size);
            }

            if (style.RuledTables)
            {
                _page.Ops.Add(new LineOp(x, _y, x, _y + height, 0.6, Rgb.Black));
            }

            x += widths[c];
        }

        if (style.RuledTables)
        {
            _page.Ops.Add(new LineOp(x, _y, x, _y + height, 0.6, Rgb.Black));
            _page.Ops.Add(new LineOp(x0, _y, x, _y, 0.6, Rgb.Black));
            _page.Ops.Add(new LineOp(x0, _y + height, x, _y + height, 0.6, Rgb.Black));
        }

        _y += height;
    }

    private void WriteFigure(FigureBlock figure)
    {
        const double height = 170;
        KeepTogether(height + 40);
        var x = ColumnX;
        var w = ColumnWidth;
        _page.Ops.Add(new RectOp(x, _y, w, height, 0.8, Rgb.Gray, Fill: false));

        // A schematic: a shaft line with parts as boxes and circles, and the labels in two rows above and
        // below it, each with a leader line to its part.
        var midY = _y + height / 2;
        _page.Ops.Add(new LineOp(x + 16, midY, x + w - 16, midY, 1.2, Rgb.Gray));
        var n = Math.Max(figure.Labels.Count, 3);
        var partX = Enumerable.Range(0, n).Select(i => x + 24 + (i + 0.5) * (w - 48) / n).ToArray();
        for (var i = 0; i < n; i++)
        {
            if (i % 2 == 0)
            {
                _page.Ops.Add(new RectOp(partX[i] - 11, midY - 26, 22, 52, 1, Rgb.Black, Fill: false));
            }
            else
            {
                _page.Ops.Add(new EllipseOp(partX[i], midY, 36, 1, Rgb.Black));
            }
        }

        var perRow = (int)Math.Ceiling(figure.Labels.Count / 2.0);
        for (var i = 0; i < figure.Labels.Count; i++)
        {
            var top = i % 2 == 0;
            var slot = i / 2;
            var labelX = x + 12 + (slot + 0.5) * (w - 24) / Math.Max(perRow, 1);
            var size = 7.5;
            var text = figure.Labels[i];
            var textWidth = Metrics.Width(text, FontKind.Regular, size);
            var baseline = top ? _y + 16 : _y + height - 8;
            _page.Ops.Add(new TextOp(labelX - textWidth / 2, baseline, text, FontKind.Regular, size, Rgb.Black));
            _page.Ops.Add(new LineOp(labelX, top ? baseline + 3 : baseline - size - 1, partX[i], top ? midY - 26 : midY + 18, 0.5, Rgb.Gray));
        }

        _y += height + 4;
        if (figure.Caption is not null)
        {
            WriteWrapped(figure.Caption, FontKind.Italic, style.BodySize - 1, 0, w, hyphenate: false);
        }

        _y += style.BodySize;
    }

    private void AddRunningText()
    {
        foreach (var page in _pages)
        {
            if (style.RunningHeader is not null)
            {
                page.Ops.Add(new TextOp(style.MarginX, style.MarginTop - 26, style.RunningHeader, FontKind.Regular, 8, Rgb.Gray));
                page.Ops.Add(new LineOp(style.MarginX, style.MarginTop - 20, style.Width - style.MarginX, style.MarginTop - 20, 0.5, Rgb.Gray));
            }

            if (style.RunningFooter is not null)
            {
                var footer = style.RunningFooter.Replace("{page}", $"{page.Number}").Replace("{pages}", $"{_pages.Count}");
                var fx = style.Width - style.MarginX - Metrics.Width(footer, FontKind.Regular, 8);
                page.Ops.Add(new TextOp(fx, style.Height - style.MarginBottom + 30, footer, FontKind.Regular, 8, Rgb.Gray));
            }
        }
    }
}
