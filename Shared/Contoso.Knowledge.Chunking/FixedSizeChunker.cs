using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Chunking;

/// <summary>
/// Fixed-size windows of tokens that overlap: the baseline every other strategy has to beat. Windows are counted in
/// the tokenizer's tokens, not characters, because model limits are. With <paramref name="snapToWords"/>, a window
/// ends at the last space or line break inside it and the next one starts at a word, so no word is cut in two.
/// </summary>
public sealed class FixedSizeChunker(Tokenizer tokenizer, int size = 200, int overlap = 30, bool snapToWords = true) : IChunker
{
    public string Name => $"fixed-{size}";

    public ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default)
    {
        var text = source.Text;
        var chunks = new List<Chunk>();
        var start = SkipSpace(text, 0);
        while (start < text.Length)
        {
            var end = Tokens.Fit(tokenizer, text, start, text.Length, size);
            if (snapToWords && end < text.Length)
            {
                var space = text.LastIndexOfAny([' ', '\n'], end - 1, end - start);
                if (space > start + (end - start) / 2) end = space;
            }

            end = Math.Max(end, start + 1);
            chunks.Add(new Chunk(source.Id, start, end, text[start..end]));
            if (end >= text.Length) break;

            // The next window starts `overlap` tokens before this one ended, at the start of a word.
            var next = start + tokenizer.GetIndexByTokenCountFromEnd(text.AsSpan(start, end - start), overlap, out _, out _);
            if (snapToWords)
            {
                while (next < end && next > 0 && !char.IsWhiteSpace(text[next - 1])) next++;
            }

            start = SkipSpace(text, Math.Max(next, start + 1));
        }

        return ValueTask.FromResult<IReadOnlyList<Chunk>>(chunks);
    }

    private static int SkipSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }
}
