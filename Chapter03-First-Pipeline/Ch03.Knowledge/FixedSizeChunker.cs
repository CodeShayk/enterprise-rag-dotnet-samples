using Contoso.Corpus;
using Microsoft.ML.Tokenizers;

namespace Ch03.Knowledge;

/// <summary>A piece of a source document: the unit the assistant indexes and retrieves.</summary>
public sealed record Chunk(string Id, SourceDocument Document, int Index, string Text, int TokenCount);

/// <summary>
/// Splits text into windows of a fixed number of tokens, each repeating the last few tokens of
/// the one before it. It ignores headings, tables and sentences, which is the point: it's the
/// baseline that Chapter 9's structure-aware strategies have to beat.
/// </summary>
public sealed class FixedSizeChunker(Tokenizer tokenizer, int chunkTokens, int overlapTokens)
{
    public IEnumerable<Chunk> Split(SourceDocument document)
    {
        var text = document.Text;
        var start = 0;
        var index = 0;
        while (start < text.Length)
        {
            // The longest stretch from 'start' that fits in chunkTokens tokens.
            var rest = text[start..];
            var length = tokenizer.GetIndexByTokenCount(rest, chunkTokens, out _, out var tokenCount);
            var window = rest[..length];
            yield return new Chunk($"{document.Id}#{index}", document, index, window.Trim(), tokenCount);

            if (start + length >= text.Length)
            {
                yield break;
            }

            // Start the next window overlapTokens tokens before this one ends.
            var overlapStart = tokenizer.GetIndexByTokenCountFromEnd(window, overlapTokens, out _, out _);
            start += Math.Max(overlapStart, 1);
            index++;
        }
    }
}
