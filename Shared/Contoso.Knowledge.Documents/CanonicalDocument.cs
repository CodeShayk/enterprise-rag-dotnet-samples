namespace Contoso.Knowledge.Documents;

/// <summary>
/// One version of one source document in a format-agnostic form: its blocks in reading order, its
/// metadata and a reference to the file it came from. Every parser produces it, every chunker reads it,
/// and the Markdown the index sees is a projection of it (<see cref="ToText"/>), not its source of truth.
/// </summary>
public sealed record CanonicalDocument(
    string Id,
    string Version,
    DocumentMetadata Metadata,
    SourceReference Source,
    IReadOnlyList<Block> Blocks)
{
    /// <summary>Bumped when the model's shape changes, so that stored documents can be upgraded (Chapter 6).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Page sizes for paged formats, so that anchors can be drawn on the page (Chapters 8 and 18).</summary>
    public IReadOnlyList<PageInfo> Pages { get; init; } = [];

    /// <summary>Which parser produced the blocks, with its version, such as <c>pdf/1.0</c>.</summary>
    public string? Parser { get; init; }

    /// <summary>
    /// The plain-text projection with the offsets of every block in it. Blocks whose role is in
    /// <paramref name="excludedRoles"/> (by default headers, footers, boilerplate, quotes and comments) are left out.
    /// </summary>
    public DocumentText ToText(IReadOnlySet<string>? excludedRoles = null)
    {
        excludedRoles ??= BlockRoles.ExcludedByDefault;
        var text = new System.Text.StringBuilder();
        var spans = new List<BlockSpan>();
        Block? previous = null;
        for (var i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            if (block.Role is not null && excludedRoles.Contains(block.Role))
            {
                continue;
            }

            if (previous is not null)
            {
                // Consecutive list items stay on adjacent lines; everything else is a paragraph apart.
                text.Append(previous is ListItemBlock && block is ListItemBlock ? "\n" : "\n\n");
            }

            var start = text.Length;
            text.Append(block.ToMarkdown());
            spans.Add(new BlockSpan(i, start, text.Length));
            previous = block;
        }

        return new DocumentText(text.ToString(), spans);
    }

    /// <summary>The headings above each block, outermost first, such as <c>6 Assembly › 6.3 Flange bolt torque</c>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> SectionPaths()
    {
        var paths = new List<IReadOnlyList<string>>(Blocks.Count);
        var open = new List<HeadingBlock>();
        foreach (var block in Blocks)
        {
            if (block is HeadingBlock heading && block.Role is null)
            {
                open.RemoveAll(h => h.Level >= heading.Level);
                open.Add(heading);
            }

            paths.Add([.. open.Select(h => h.Text)]);
        }

        return paths;
    }
}

/// <summary>The text projection of a document and where each block sits in it.</summary>
public sealed record DocumentText(string Text, IReadOnlyList<BlockSpan> Blocks)
{
    /// <summary>The block that contains the character at <paramref name="offset"/>, or the next one after a separator.</summary>
    public BlockSpan? BlockAt(int offset) =>
        Blocks.FirstOrDefault(b => offset >= b.Start && offset < b.End) ?? Blocks.FirstOrDefault(b => b.Start >= offset);
}

/// <summary>Block <see cref="Index"/> of the document occupies characters [<see cref="Start"/>, <see cref="End"/>) of the text.</summary>
public sealed record BlockSpan(int Index, int Start, int End);

/// <summary>A page's number (from 1) and size in points (1/72 inch).</summary>
public sealed record PageInfo(int Number, double Width, double Height)
{
    /// <summary>False for a scanned page with no text layer: its text has to come from OCR (Chapter 8).</summary>
    public bool HasTextLayer { get; init; } = true;
}

/// <summary>
/// Where a block came from. Boxes are in points with the origin at the top left of the page,
/// which is how images are addressed; PDF's own coordinates start at the bottom left.
/// </summary>
public sealed record SourceAnchor
{
    public int? Page { get; init; }

    public BoundingBox? Box { get; init; }

    /// <summary>A location for formats without pages: <c>slide 3/notes</c>, <c>sheet Fluid!A4:H40</c>, <c>attachment SR-2026-1107.pdf</c>.</summary>
    public string? Path { get; init; }

    /// <summary>The extraction's confidence from 0 to 1, when the extractor reports one (OCR does).</summary>
    public double? Confidence { get; init; }
}

public readonly record struct BoundingBox(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;
}

/// <summary>The file a document version came from, as its source system described it when it was fetched.</summary>
public sealed record SourceReference(string System, string Uri)
{
    public string? MediaType { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public long? Length { get; init; }

    /// <summary>The SHA-256 of the file's bytes. Chapter 6 also hashes the normalized text, which survives a re-save.</summary>
    public string? ContentHash { get; init; }
}
