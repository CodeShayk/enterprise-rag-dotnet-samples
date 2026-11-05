using System.Security.Cryptography;
using System.Text;
using Ch06.Ingestion.Worker.Dedup;
using Contoso.Knowledge.Documents;
using Microsoft.ML.Tokenizers;

namespace Ch06.Ingestion.Worker.Pipeline;

/// <summary>A chunk before storage: its stable ID, text, section path and where it sits in the document's text.</summary>
public sealed record ChunkDraft(string ChunkId, string Text, string Section, int Start, int End, int FirstBlock, int LastBlock);

/// <summary>
/// Packs whole blocks into chunks of up to a token budget and names each chunk by a hash of its content. A new version
/// that changes one section changes only that section's chunk IDs, so only those chunks are embedded again.
/// Chapter 9 compares chunking strategies; this one only has to be stable.
/// </summary>
public sealed class StableChunker(Tokenizer tokenizer, int maxTokens = 200)
{
    public IReadOnlyList<ChunkDraft> Chunk(CanonicalDocument document)
    {
        var text = document.ToText();
        var paths = document.SectionPaths();
        var chunks = new List<ChunkDraft>();
        var pending = new List<BlockSpan>();
        var tokens = 0;

        void Flush()
        {
            if (pending.Count == 0) return;
            var start = pending[0].Start;
            var end = pending[^1].End;
            var chunkText = text.Text[start..end];
            var section = string.Join(" › ", paths[pending[0].Index]);
            chunks.Add(new ChunkDraft(Id(section, chunkText), chunkText, section, start, end, pending[0].Index, pending[^1].Index));
            pending.Clear();
            tokens = 0;
        }

        foreach (var span in text.Blocks)
        {
            var count = tokenizer.CountTokens(text.Text.AsSpan(span.Start, span.End - span.Start));
            // A heading starts a new chunk, so that a section's text stays with its own heading.
            if (pending.Count > 0 && (tokens + count > maxTokens || document.Blocks[span.Index] is HeadingBlock))
            {
                Flush();
            }

            pending.Add(span);
            tokens += count;
        }

        Flush();
        return chunks;
    }

    /// <summary>
    /// 16 hex digits of the SHA-256 of the normalized section path and text. The ID depends on the content alone,
    /// so an unchanged chunk keeps its ID across versions, and identical chunks in duplicate files are stored once.
    /// </summary>
    public static string Id(string section, string text) =>
        $"ch-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprints.Normalize($"{section} {text}"))))[..16]}";
}
