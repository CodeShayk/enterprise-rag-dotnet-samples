using Contoso.Knowledge.Chunking;

namespace Ch10.Embeddings.Cli;

/// <summary>Chapter 9's enrichment wrapper, copied: context added to every chunk an inner strategy makes, for indexing only.</summary>
public sealed class EnrichedChunker(IChunker inner, string suffix, Func<Chunk, Chunk> enrich) : IChunker
{
    public string Name => $"{inner.Name}+{suffix}";

    public async ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default) =>
        [.. (await inner.SplitAsync(source, cancellationToken)).Select(enrich)];
}
