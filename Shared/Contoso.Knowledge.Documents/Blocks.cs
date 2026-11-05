using System.Text.Json.Serialization;

namespace Contoso.Knowledge.Documents;

/// <summary>
/// One unit of content in reading order. Every block can say where it came from
/// (<see cref="Anchor"/>) and what it is besides content (<see cref="Role"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HeadingBlock), "heading")]
[JsonDerivedType(typeof(ParagraphBlock), "paragraph")]
[JsonDerivedType(typeof(ListItemBlock), "list-item")]
[JsonDerivedType(typeof(TableBlock), "table")]
[JsonDerivedType(typeof(FigureBlock), "figure")]
[JsonDerivedType(typeof(CodeBlock), "code")]
public abstract record Block
{
    /// <summary>Where the block came from: a page and box, or a path such as a slide, a cell range or an attachment.</summary>
    public SourceAnchor? Anchor { get; init; }

    /// <summary>
    /// <see langword="null"/> for body content. Otherwise one of <see cref="BlockRoles"/>: a page header or footer,
    /// navigation boilerplate, a quoted reply, speaker notes, a reviewer's comment. Projections leave some roles out.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>The block as Markdown, which is also the plain-text projection the index sees.</summary>
    public abstract string ToMarkdown();
}

/// <summary>The roles a block can have besides body content.</summary>
public static class BlockRoles
{
    public const string Header = "header";
    public const string Footer = "footer";
    public const string Boilerplate = "boilerplate";
    public const string Quote = "quote";
    public const string SpeakerNotes = "speaker-notes";
    public const string Comment = "comment";

    /// <summary>A mark added on paper: a stamp, a signature or a handwritten note (Chapter 8). Kept in the projection.</summary>
    public const string Annotation = "annotation";

    /// <summary>Roles left out of the text projection by default: they repeat, distract or aren't the author's content.</summary>
    public static IReadOnlySet<string> ExcludedByDefault { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Header, Footer, Boilerplate, Quote, Comment };
}

public sealed record HeadingBlock(int Level, string Text) : Block
{
    public override string ToMarkdown() => $"{new string('#', Math.Clamp(Level, 1, 6))} {Text}";
}

public sealed record ParagraphBlock(string Text) : Block
{
    public override string ToMarkdown() => Text;
}

/// <summary>A list item. <see cref="Marker"/> keeps the source's numbering ("1.", "a)") so that steps stay numbered.</summary>
public sealed record ListItemBlock(string Text, int Level = 1, string? Marker = null) : Block
{
    public override string ToMarkdown() => $"{new string(' ', 2 * (Math.Max(Level, 1) - 1))}{Marker ?? "-"} {Text}";
}

/// <summary>
/// A table as rows of cell text. The first <see cref="HeaderRows"/> rows are headers; merged cells are
/// repeated into every cell they cover, so each row can be read on its own.
/// </summary>
public sealed record TableBlock(IReadOnlyList<IReadOnlyList<string>> Rows, int HeaderRows = 1) : Block
{
    public string? Caption { get; init; }

    public override string ToMarkdown()
    {
        var lines = new List<string>();
        if (Caption is not null)
        {
            lines.Add(Caption);
            lines.Add("");
        }

        var columns = Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
        var headerRows = Math.Clamp(HeaderRows, 1, Math.Max(Rows.Count, 1));
        for (var i = 0; i < Rows.Count; i++)
        {
            var cells = Enumerable.Range(0, columns).Select(c => c < Rows[i].Count ? Escape(Rows[i][c]) : "");
            lines.Add($"| {string.Join(" | ", cells)} |");
            if (i == headerRows - 1)
            {
                lines.Add($"|{string.Concat(Enumerable.Repeat("---|", columns))}");
            }
        }

        return string.Join('\n', lines);
    }

    private static string Escape(string cell) => cell.Replace("|", "\\|").ReplaceLineEndings(" ").Trim();
}

/// <summary>A figure: its caption, alternative text and the text labels drawn inside it.</summary>
public sealed record FigureBlock(string? Caption) : Block
{
    public string? AltText { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public override string ToMarkdown()
    {
        var parts = new List<string>();
        if (Caption is not null) parts.Add(Caption);
        if (AltText is not null) parts.Add(AltText);
        if (Labels.Count > 0) parts.Add($"Labels: {string.Join("; ", Labels)}");
        return string.Join('\n', parts);
    }
}

public sealed record CodeBlock(string Text, string? Language = null) : Block
{
    public override string ToMarkdown() => $"```{Language}\n{Text}\n```";
}
