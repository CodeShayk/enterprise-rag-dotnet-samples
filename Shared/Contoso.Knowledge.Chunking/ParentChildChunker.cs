using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Chunking;

/// <summary>
/// Parent-child retrieval: index small, return large. Parents are structure-aware chunks of up to
/// <paramref name="parent"/> tokens; children are runs of sentences of up to <paramref name="child"/> tokens inside a
/// parent, and a matching child returns its parent. With <paramref name="window"/> above zero, each child is one
/// sentence and returns itself and that many sentences on either side (sentence-window retrieval).
/// </summary>
public sealed class ParentChildChunker(Tokenizer tokenizer, int child = 100, int parent = 600, int window = 0) : IChunker
{
    private readonly StructureChunker _parents = new(tokenizer, parent);

    public string Name => window > 0 ? $"sentence-window-{window}" : $"parent-child-{child}-{parent}";

    public async ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default)
    {
        var text = source.Text;
        var children = new List<Chunk>();
        foreach (var p in await _parents.SplitAsync(source, cancellationToken))
        {
            var sentences = Sentences.Split(text, p.Start, p.End);
            if (window > 0)
            {
                for (var i = 0; i < sentences.Count; i++)
                {
                    var (s, e) = sentences[i];
                    var around = (sentences[Math.Max(0, i - window)].Start, sentences[Math.Min(sentences.Count - 1, i + window)].End);
                    children.Add(new Chunk(source.Id, s, e, text[s..e]) { Header = p.Header, Kind = "sentence", Returns = around });
                }

                continue;
            }

            // Pack sentences into children; a table cut into rows keeps its row chunks as the children.
            if (p.Kind == "table-rows")
            {
                children.Add(p with { Returns = (p.Start, p.End) });
                continue;
            }

            var first = 0;
            var tokens = 0;
            for (var i = 0; i < sentences.Count; i++)
            {
                var count = Tokens.Count(tokenizer, text, sentences[i].Start, sentences[i].End);
                if (i > first && tokens + count > child)
                {
                    children.Add(Child(p, sentences[first].Start, sentences[i - 1].End));
                    (first, tokens) = (i, 0);
                }

                tokens += count;
            }

            if (sentences.Count > 0) children.Add(Child(p, sentences[first].Start, sentences[^1].End));
        }

        return children;

        Chunk Child(Chunk p, int s, int e) => new(source.Id, s, e, text[s..e]) { Header = p.Header, Kind = "child", Returns = (p.Start, p.End) };
    }
}
