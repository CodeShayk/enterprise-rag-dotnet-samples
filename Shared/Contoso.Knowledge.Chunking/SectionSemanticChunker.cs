using System.Numerics.Tensors;
using Contoso.Knowledge.Documents;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Chunking;

/// <summary>
/// Semantic breakpoints inside the document's structure. A section that fits the limit is one chunk, as in
/// <see cref="StructureChunker"/>. A longer section is cut only between blocks, never right after a heading, at the
/// largest semantic distance between neighboring blocks, and cut again until every piece fits; a piece keeps at least
/// <paramref name="min"/> tokens when it can. A long table still becomes key-value rows. Only the blocks of long
/// sections are embedded, so the cost follows how much of the corpus needs cutting.
/// </summary>
public sealed class SectionSemanticChunker(
    IEmbeddingGenerator<string, Embedding<float>> embedder, Tokenizer tokenizer, int max = 300, int min = 40, string prefix = "", int batch = 32) : IChunker
{
    public string Name => $"structure-semantic-{max}";

    /// <summary>Blocks embedded and sections cut so far, for the CPU budget.</summary>
    public int Embedded { get; private set; }

    public int SectionsCut { get; private set; }

    public async ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default)
    {
        var text = source.Text;
        var chunks = new List<Chunk>();
        foreach (var section in Structure.Sections(source))
        {
            var header = source.Breadcrumb(Structure.PathBlock(source, section));
            var (start, end) = (section[0].Start, section[^1].End);
            if (Tokens.Count(tokenizer, text, start, end) <= max)
            {
                chunks.Add(new Chunk(source.Id, start, end, text[start..end]) { Header = header, Kind = "section" });
                continue;
            }

            SectionsCut++;
            var run = new List<BlockSpan>();
            foreach (var block in section)
            {
                if (source.Document.Blocks[block.Index] is TableBlock table && Tokens.Count(tokenizer, text, block.Start, block.End) > max)
                {
                    await CutAsync(run, header);
                    run.Clear();
                    chunks.AddRange(Structure.TableRows(source, block, table, header, tokenizer, max));
                    continue;
                }

                run.Add(block);
            }

            await CutAsync(run, header);
        }

        return chunks;

        async Task CutAsync(List<BlockSpan> blocks, string header)
        {
            if (blocks.Count == 0) return;
            var vectors = new List<Embedding<float>>(blocks.Count);
            foreach (var group in blocks.Chunk(batch))
                vectors.AddRange(await embedder.GenerateAsync([.. group.Select(b => prefix + text[b.Start..b.End])], cancellationToken: cancellationToken));
            Embedded += blocks.Count;
            var distances = Enumerable.Range(0, blocks.Count - 1)
                .Select(i => 1 - TensorPrimitives.CosineSimilarity(vectors[i].Vector.Span, vectors[i + 1].Vector.Span)).ToArray();
            Emit(0, blocks.Count - 1);

            // Cut blocks first..last after the block with the largest distance to its neighbor until every piece fits.
            void Emit(int first, int last)
            {
                var (s, e) = (blocks[first].Start, blocks[last].End);
                var cuts = Enumerable.Range(first, last - first).Where(i => source.Document.Blocks[blocks[i].Index] is not HeadingBlock).ToList();
                if (cuts.Count == 0 || Tokens.Count(tokenizer, text, s, e) <= max)
                {
                    chunks.Add(new Chunk(source.Id, s, e, text[s..e]) { Header = header });
                    return;
                }

                var balanced = cuts.Where(i => Tokens.Count(tokenizer, text, s, blocks[i].End) >= min && Tokens.Count(tokenizer, text, blocks[i + 1].Start, e) >= min).ToList();
                var cut = (balanced.Count > 0 ? balanced : cuts).MaxBy(i => distances[i]);
                Emit(first, cut);
                Emit(cut + 1, last);
            }
        }
    }
}
