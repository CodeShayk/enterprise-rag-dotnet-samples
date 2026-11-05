using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Chunking;

/// <summary>
/// Context added to a chunk for indexing only; the span it returns doesn't change. Breadcrumbs come from the
/// structure-aware chunker. Entity tags cost a regular expression. Model-written context and hypothetical questions
/// cost a model call per chunk, so they run in batches, on a budget, and only where they're measured to help.
/// </summary>
public static partial class Enrichment
{
    /// <summary>Contoso's identifiers in the chunk: part numbers, equipment tags, change notices, error and fault codes, procedures, work orders.</summary>
    public static IReadOnlyList<string> Entities(string text) =>
        [.. Identifier().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal)];

    public static Chunk WithEntities(Chunk chunk) => Entities(chunk.Text) is { Count: > 0 } tags
        ? chunk with { Header = Join(chunk.Header, "Mentions: " + string.Join(", ", tags)) }
        : chunk;

    /// <summary>
    /// Contextual retrieval: a model reads the document and writes one or two sentences that situate the chunk in
    /// it, which are indexed with the chunk. The document comes first in the prompt so that a server that caches a
    /// prompt's prefix processes it once for all of the document's chunks.
    /// </summary>
    public static async Task<Chunk> WithModelContextAsync(Chunk chunk, ChunkSource source, IChatClient chat, int documentTokens, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            <document title="{source.Title}">
            {source.Text[..Math.Min(source.Text.Length, documentTokens * 4)]}
            </document>
            Here is a chunk from that document:
            <chunk>
            {chunk.Text}
            </chunk>
            Write one or two short sentences that situate this chunk within the document, to improve search retrieval of the
            chunk. Name the document, the equipment and the topic. Answer with the sentences only.
            """;
        var response = await chat.GetResponseAsync(prompt, new ChatOptions { Temperature = 0, MaxOutputTokens = 80 }, cancellationToken);
        return chunk with { Header = Join(chunk.Header, response.Text.Trim()) };
    }

    private static string Join(string? header, string addition) => header is null ? addition : $"{header}\n{addition}";

    [GeneratedRegex(@"\b(?:PN-\d{4}-[A-Z]|[PV]-\d{3}|GBX-\d{2}|HG-\d{3,4}|ECN-\d{4}|E-?\d{3}|F\d{3}|SOP-[A-Z]{2}-\d{3}|WI-[A-Z]{2}-\d{3}|WO-\d{5}|SB-\d{4}|SC-\d{4}-\d{3})\b")]
    private static partial Regex Identifier();
}
