using Contoso.Knowledge.Documents;

namespace Contoso.Corpus.Generator.Content;

/// <summary>Builds the truth for one source document: the canonical blocks a writer renders.</summary>
internal sealed class DocBuilder(string id, string version, DocumentMetadata metadata)
{
    private readonly List<Block> _blocks = [];

    public int Count => _blocks.Count;

    public DocBuilder H(int level, string text) => Add(new HeadingBlock(level, text));

    public DocBuilder P(string text) => Add(new ParagraphBlock(text));

    public DocBuilder P(params string[] sentences) => Add(new ParagraphBlock(string.Join(' ', sentences)));

    public DocBuilder Steps(params string[] steps)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            Add(new ListItemBlock(steps[i], 1, $"{i + 1}."));
        }

        return this;
    }

    public DocBuilder Bullets(params string[] items)
    {
        foreach (var item in items)
        {
            Add(new ListItemBlock(item, 1, "-"));
        }

        return this;
    }

    public DocBuilder Table(string? caption, string[] header, params string[][] rows) =>
        Add(new TableBlock([header, .. rows]) { Caption = caption });

    public DocBuilder Figure(string caption, string altText, params string[] labels) =>
        Add(new FigureBlock(caption) { AltText = altText, Labels = labels });

    public DocBuilder Add(Block block)
    {
        _blocks.Add(block);
        return this;
    }

    /// <summary>
    /// Appends a seed document from <c>Contoso.Corpus/Markdown</c> verbatim, so that the golden set's
    /// version 1 spans also locate in the longer document. Headings move by <paramref name="headingShift"/>.
    /// </summary>
    public DocBuilder Seed(string seedFile, int headingShift = 0, bool skipTitle = false)
    {
        var blocks = SeedText.Blocks(seedFile);
        foreach (var block in skipTitle ? blocks.Skip(1) : blocks)
        {
            Add(block is HeadingBlock h ? h with { Level = h.Level + headingShift } : block);
        }

        return this;
    }

    public CanonicalDocument Build(string system, string relativePath) =>
        new(id, version, metadata, new SourceReference(system, relativePath.Replace('\\', '/'))
        {
            MediaType = MediaTypes.FromFileName(relativePath),
        }, [.. _blocks]);
}

/// <summary>Reads the Markdown seed documents (Chapter 3's edition of the corpus) as canonical blocks.</summary>
internal static class SeedText
{
    private static readonly Dictionary<string, SourceDocument> Seeds =
        MarkdownCorpus.Load().ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The seed document whose file name (without <c>.md</c>) or ID is <paramref name="name"/>.</summary>
    public static SourceDocument Document(string name)
    {
        var path = Path.Combine(MarkdownCorpus.Folder, name + ".md");
        return File.Exists(path) ? MarkdownCorpus.Parse(File.ReadAllText(path), path) : Seeds[name];
    }

    /// <summary>One sentence or line from a seed document, checked to exist so that copied text can't drift.</summary>
    public static string Sentence(string name, string startsWith)
    {
        var text = Document(name).Text;
        var start = text.IndexOf(startsWith, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Seed {name} has no text starting with \"{startsWith}\".");
        }

        var end = text.IndexOfAny(['\n'], start);
        var line = end < 0 ? text[start..] : text[start..end];
        var stop = line.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? line.TrimEnd() : line[..(stop + 1)];
    }

    /// <summary>A line-based reading of the seed Markdown: headings, paragraphs, list items and pipe tables.</summary>
    public static IReadOnlyList<Block> Blocks(string name)
    {
        var lines = Document(name).Text.ReplaceLineEndings("\n").Split('\n');
        var blocks = new List<Block>();
        var paragraph = new List<string>();
        var table = new List<IReadOnlyList<string>>();

        void Flush()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new ParagraphBlock(string.Join(' ', paragraph)));
                paragraph.Clear();
            }

            if (table.Count > 0)
            {
                blocks.Add(new TableBlock([.. table]));
                table.Clear();
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                Flush();
            }
            else if (line.StartsWith('#'))
            {
                Flush();
                var level = line.TakeWhile(c => c == '#').Count();
                blocks.Add(new HeadingBlock(level, line[level..].Trim()));
            }
            else if (line.StartsWith('|'))
            {
                if (paragraph.Count > 0) Flush();
                if (!line.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Any())
                {
                    continue;
                }

                table.Add([.. line.Trim('|').Split('|').Select(c => c.Trim())]);
            }
            else if (System.Text.RegularExpressions.Regex.Match(line, @"^(\d+\.|-) (.*)$") is { Success: true } item)
            {
                Flush();
                blocks.Add(new ListItemBlock(item.Groups[2].Value, 1, item.Groups[1].Value));
            }
            else
            {
                paragraph.Add(line.Trim());
            }
        }

        Flush();
        return blocks;
    }
}

/// <summary>Shorthand for metadata, so that content files read like a document register.</summary>
internal static class Meta
{
    public static DocumentMetadata Of(
        string title, string tenant, string type, string sensitivity, string[] groups, string owner,
        string? revision = null, DateOnly? from = null, DateOnly? to = null,
        string[]? supersedes = null, string[]? tags = null, string[]? parts = null) => new()
        {
            Title = title,
            Tenant = tenant,
            DocumentType = type,
            Sensitivity = sensitivity,
            AllowedGroups = groups,
            Owner = owner,
            Revision = revision,
            EffectiveFrom = from,
            EffectiveTo = to,
            Supersedes = supersedes ?? [],
            EquipmentTags = tags ?? [],
            PartNumbers = parts ?? [],
        };

    public static DateOnly D(int year, int month, int day) => new(year, month, day);
}
