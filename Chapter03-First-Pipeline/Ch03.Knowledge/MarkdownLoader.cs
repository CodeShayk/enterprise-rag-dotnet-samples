using Contoso.Corpus;

namespace Ch03.Knowledge;

/// <summary>
/// Loads Markdown files into source documents. Each file starts with front matter: a line of
/// three dashes, one <c>key: value</c> line each for <c>id</c>, <c>title</c>, <c>tenant</c> and
/// <c>groups</c> (comma-separated), and another line of three dashes.
/// </summary>
public static class MarkdownLoader
{
    public static IReadOnlyList<SourceDocument> LoadFolder(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*.md")
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

        // The permissions travel with the text from the moment it's loaded.
        return new SourceDocument(
            Id: meta["id"],
            Title: meta["title"],
            Tenant: meta["tenant"],
            AllowedGroups: meta["groups"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Text: string.Join('\n', lines[(end + 1)..]).Trim());
    }
}
