using Contoso.Knowledge.Documents;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Chunking;

/// <summary>
/// A piece of a document to index. <see cref="Start"/> and <see cref="End"/> locate it in the document's text
/// projection, so every chunk can be cited and scored. <see cref="Text"/> is what gets indexed and may differ
/// from the span: a table cut into rows is indexed as key-value rows. <see cref="Header"/> is context added for
/// indexing only. <see cref="Returns"/> is the span handed to the model when the chunk matches, which for
/// parent-child retrieval is larger than the chunk.
/// </summary>
public sealed record Chunk(string Doc, int Start, int End, string Text)
{
    public string? Header { get; init; }

    public (int Start, int End)? Returns { get; init; }

    /// <summary>passage, section, summary, table-rows, sentence and so on.</summary>
    public string Kind { get; init; } = "passage";

    public string IndexText => Header is null ? Text : $"{Header}\n{Text}";

    public int ReturnStart => Returns?.Start ?? Start;

    public int ReturnEnd => Returns?.End ?? End;
}

/// <summary>A document ready to chunk: its canonical model, text projection and the section path of every block.</summary>
public sealed class ChunkSource(string id, string title, CanonicalDocument document)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public CanonicalDocument Document { get; } = document;

    public DocumentText Projection { get; } = document.ToText();

    public IReadOnlyList<IReadOnlyList<string>> SectionPaths { get; } = document.SectionPaths();

    public string Text => Projection.Text;

    /// <summary>"Title › Section › Subsection" for the block at <paramref name="index"/>: where a chunk sits in its document.</summary>
    public string Breadcrumb(int index) => string.Join(" › ", SectionPaths[index].Prepend(Title).Distinct());
}

/// <summary>One chunking strategy.</summary>
public interface IChunker
{
    string Name { get; }

    ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default);
}

/// <summary>Token counts with the tokenizer the budget is written in.</summary>
public static class Tokens
{
    public static int Count(Tokenizer tokenizer, string text, int start, int end) => tokenizer.CountTokens(text.AsSpan(start, end - start));

    /// <summary>The end of the longest prefix of text[start..limit) that fits in <paramref name="max"/> tokens.</summary>
    public static int Fit(Tokenizer tokenizer, string text, int start, int limit, int max) =>
        start + tokenizer.GetIndexByTokenCount(text.AsSpan(start, limit - start), max, out _, out _);
}
