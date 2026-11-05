using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Parsing;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Chunking;

/// <summary>
/// Structure-aware chunking from the canonical model. A section, a heading with the blocks under it, is one chunk
/// when it fits. A longer section is packed block by block, so a paragraph, a list item or a table row is never
/// cut. A table too long for one chunk becomes groups of rows indexed as key-value rows, each row carrying its
/// column headers. Every chunk is indexed under its breadcrumb, "Title › Section › Subsection".
/// </summary>
public sealed class StructureChunker(Tokenizer tokenizer, int max = 300, bool breadcrumbs = true) : IChunker
{
    public string Name => breadcrumbs ? $"structure-{max}" : $"structure-{max}-bare";

    public ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default)
    {
        var text = source.Text;
        var chunks = new List<Chunk>();
        foreach (var section in Structure.Sections(source))
        {
            var header = breadcrumbs ? source.Breadcrumb(Structure.PathBlock(source, section)) : null;
            var (start, end) = (section[0].Start, section[^1].End);
            if (Tokens.Count(tokenizer, text, start, end) <= max)
            {
                chunks.Add(new Chunk(source.Id, start, end, text[start..end]) { Header = header, Kind = "section" });
                continue;
            }

            var pending = new List<BlockSpan>();
            var tokens = 0;
            void Flush()
            {
                if (pending.Count == 0) return;
                var (s, e) = (pending[0].Start, pending[^1].End);
                chunks.Add(new Chunk(source.Id, s, e, text[s..e]) { Header = header });
                pending.Clear();
                tokens = 0;
            }

            foreach (var block in section)
            {
                var count = Tokens.Count(tokenizer, text, block.Start, block.End);
                if (source.Document.Blocks[block.Index] is TableBlock table && count > max)
                {
                    Flush();
                    chunks.AddRange(Structure.TableRows(source, block, table, header, tokenizer, max));
                    continue;
                }

                if (pending.Count > 0 && tokens + count > max) Flush();
                pending.Add(block);
                tokens += count;
            }

            Flush();
        }

        return ValueTask.FromResult<IReadOnlyList<Chunk>>(chunks);
    }
}

/// <summary>Sections and table rows, shared by the strategies that read the canonical model.</summary>
public static class Structure
{
    /// <summary>
    /// A heading and the blocks after it, up to the next heading. A heading followed directly by another heading
    /// joins the next section, and blocks before the first heading are a section of their own.
    /// </summary>
    public static IEnumerable<List<BlockSpan>> Sections(ChunkSource source)
    {
        var current = new List<BlockSpan>();
        foreach (var span in source.Projection.Blocks)
        {
            var isHeading = source.Document.Blocks[span.Index] is HeadingBlock;
            var onlyHeadings = current.All(b => source.Document.Blocks[b.Index] is HeadingBlock);
            if (isHeading && current.Count > 0 && !onlyHeadings)
            {
                yield return current;
                current = [];
            }

            current.Add(span);
        }

        if (current.Count > 0) yield return current;
    }

    /// <summary>The block whose section path names a section: its first block after the headings, so stacked headings all count.</summary>
    public static int PathBlock(ChunkSource source, List<BlockSpan> section) =>
        section.FirstOrDefault(b => source.Document.Blocks[b.Index] is not HeadingBlock)?.Index ?? section[^1].Index;

    /// <summary>
    /// A long table as groups of rows that fit the limit. The span covers the rows' lines in the projection; the
    /// indexed text is the caption and one key-value line per row ("Part › Part number: PN-4471-C; ...").
    /// </summary>
    public static IEnumerable<Chunk> TableRows(ChunkSource source, BlockSpan block, TableBlock table, string? header, Tokenizer tokenizer, int max)
    {
        var text = source.Text;
        var lines = new List<(int Start, int End)>();
        for (var at = block.Start; at < block.End;)
        {
            var end = text.IndexOf('\n', at, block.End - at);
            if (end < 0) end = block.End;
            if (text.AsSpan(at, end - at).StartsWith("| ")) lines.Add((at, end));
            at = end + 1;
        }

        var rows = TableSerializer.KeyValueRows(table).ToList();
        var data = lines.Skip(lines.Count - rows.Count).ToList();
        var caption = table.Caption is null ? "" : table.Caption + "\n";
        var group = new List<int>();
        var tokens = 0;
        foreach (var i in Enumerable.Range(0, rows.Count))
        {
            var count = tokenizer.CountTokens(rows[i]);
            if (group.Count > 0 && tokens + count > max)
            {
                yield return Rows(group);
                group = [];
                tokens = 0;
            }

            group.Add(i);
            tokens += count;
        }

        if (group.Count > 0) yield return Rows(group);

        Chunk Rows(List<int> g) =>
            new(source.Id, data[g[0]].Start, data[g[^1]].End, caption + string.Join('\n', g.Select(i => rows[i]))) { Header = header, Kind = "table-rows" };
    }
}
