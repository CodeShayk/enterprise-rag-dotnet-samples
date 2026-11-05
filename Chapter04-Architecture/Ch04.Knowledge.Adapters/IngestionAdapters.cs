using System.Runtime.CompilerServices;
using Ch04.Knowledge.Core;
using Ch04.Knowledge.Core.Ports;
using Contoso.Corpus;
using Microsoft.ML.Tokenizers;

namespace Ch04.Knowledge.Adapters;

/// <summary>Reads Markdown files with front matter (Chapter 3's loader behind the <see cref="IDocumentSource"/> port).</summary>
public sealed class MarkdownFolderSource(string folder) : IDocumentSource
{
    public async IAsyncEnumerable<SourceDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.Ordinal))
        {
            yield return Parse(await File.ReadAllTextAsync(path, cancellationToken), path);
        }
    }

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

/// <summary>Fixed-size windows of tokens with overlap (Chapter 3's chunker behind the <see cref="IChunker"/> port).</summary>
public sealed class FixedSizeChunker(Tokenizer tokenizer, int chunkTokens, int overlapTokens) : IChunker
{
    public IEnumerable<Chunk> Split(SourceDocument document)
    {
        var text = document.Text;
        var start = 0;
        var index = 0;
        while (start < text.Length)
        {
            var rest = text[start..];
            var length = tokenizer.GetIndexByTokenCount(rest, chunkTokens, out _, out _);
            var window = rest[..length];
            yield return new Chunk($"{document.Id}#{index}", document, index, window.Trim());

            if (start + length >= text.Length)
            {
                yield break;
            }

            start += Math.Max(tokenizer.GetIndexByTokenCountFromEnd(window, overlapTokens, out _, out _), 1);
            index++;
        }
    }
}
