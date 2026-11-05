namespace Contoso.Corpus;

/// <summary>
/// The Markdown edition of Contoso's corpus: 24 short documents across the three tenants and the
/// corporate library, each with front matter that names its ID, title, tenant and allowed groups.
/// The files are copied to <c>corpus/markdown</c> next to every app that references this library.
/// </summary>
public static class MarkdownCorpus
{
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "corpus", "markdown");

    /// <summary>
    /// Loads every document in the folder (Chapter 3 builds the same loader step by step; later
    /// chapters use this one so that every chapter reads the corpus the same way).
    /// </summary>
    public static IReadOnlyList<SourceDocument> Load(string? folder = null) =>
        [.. Directory.EnumerateFiles(folder ?? Folder, "*.md")
            .Order(StringComparer.Ordinal)
            .Select(path => Parse(File.ReadAllText(path), path))];

    public static SourceDocument Parse(string markdown, string path = "")
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var end = lines.Length > 0 && lines[0] == "---" ? Array.IndexOf(lines, "---", 1) : -1;
        if (end < 0)
        {
            throw new InvalidDataException($"{path}: a document needs front matter between two '---' lines.");
        }

        var meta = lines[1..end]
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

        return new SourceDocument(meta["id"], meta["title"], meta["tenant"],
            meta["groups"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            string.Join('\n', lines[(end + 1)..]).Trim());
    }
}
