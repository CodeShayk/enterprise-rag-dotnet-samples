using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Chunking;

/// <summary>
/// Recursive splitting: cut at the coarsest separator the text has (blank lines, then line breaks, then sentence
/// ends, then clause marks, then spaces), cut any piece that is still too long with the next separator, then pack
/// neighboring pieces back together up to the limit. A last piece below the minimum joins the chunk before it.
/// </summary>
public sealed class RecursiveChunker(Tokenizer tokenizer, int max = 200, int min = 40, IReadOnlyList<string>? separators = null) : IChunker
{
    private readonly IReadOnlyList<string> _separators = separators ?? ["\n\n", "\n", ". ", "; ", ", ", " "];

    public string Name => $"recursive-{max}";

    public ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default)
    {
        var text = source.Text;
        var pieces = new List<(int Start, int End)>();
        Cut(text, 0, text.Length, 0, pieces);

        var spans = new List<(int Start, int End, int Tokens)>();
        foreach (var (start, end) in pieces)
        {
            var tokens = Tokens.Count(tokenizer, text, start, end);
            if (spans.Count > 0 && spans[^1].Tokens + tokens <= max) spans[^1] = (spans[^1].Start, end, spans[^1].Tokens + tokens);
            else spans.Add((start, end, tokens));
        }

        if (spans.Count > 1 && spans[^1].Tokens < min && spans[^2].Tokens + spans[^1].Tokens <= max + min)
        {
            spans[^2] = (spans[^2].Start, spans[^1].End, spans[^2].Tokens + spans[^1].Tokens);
            spans.RemoveAt(spans.Count - 1);
        }

        return ValueTask.FromResult<IReadOnlyList<Chunk>>([.. spans.Select(s => new Chunk(source.Id, s.Start, s.End, text[s.Start..s.End]))]);
    }

    private void Cut(string text, int start, int end, int level, List<(int, int)> pieces)
    {
        (start, end) = Trim(text, start, end);
        if (end <= start) return;
        if (level >= _separators.Count || Tokens.Count(tokenizer, text, start, end) <= max)
        {
            pieces.Add((start, end));
            return;
        }

        var separator = _separators[level];
        var from = start;
        for (var at = text.IndexOf(separator, start, end - start, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(separator, from, end - from, StringComparison.Ordinal))
        {
            // The separator stays with the piece on its left, so a sentence keeps its full stop.
            var cut = at + separator.TrimEnd().Length;
            Cut(text, from, cut, level + 1, pieces);
            from = at + separator.Length;
            if (from >= end) break;
        }

        Cut(text, from, end, level + 1, pieces);
    }

    private static (int, int) Trim(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end);
    }
}
