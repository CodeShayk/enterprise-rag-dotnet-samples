using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Retrieval;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Generation;

/// <summary>
/// Text the model reads and the range of its source document it came from. A verbatim segment is exactly that range
/// of the document; a table row rendered with its column names isn't, so a quote from it maps to the whole row.
/// </summary>
public sealed record EvidenceSegment(string Text, int Start, int End, bool Verbatim = true);

/// <summary>
/// One block of evidence as the model sees it. <see cref="Id"/> is the short label the model cites (S1, S2 ...);
/// the document, section and span behind it are the stable reference, and <see cref="Probability"/> is Chapter 12's
/// calibrated probability that the block is relevant to the question.
/// </summary>
public sealed record EvidenceBlock(
    string Id, string DocumentId, string Title, string? Section, string DocumentType, DateOnly? EffectiveFrom,
    IReadOnlyList<EvidenceSegment> Segments, double Probability)
{
    public string Text => string.Join('\n', Segments.Select(s => s.Text));

    /// <summary>Where the block sits in its document: the same however the evidence is ranked or numbered.</summary>
    public string Reference => $"{DocumentId}:{Segments.Min(s => s.Start)}-{Segments.Max(s => s.End)}";

    /// <summary>The tokens the block costs in a prompt: its text and the title and section that label it.</summary>
    public int CountTokens(Tokenizer tokenizer) =>
        tokenizer.CountTokens(Text) + tokenizer.CountTokens(Title) + (Section is null ? 0 : tokenizer.CountTokens(Section));
}

public enum EvidenceOrder { BestFirst, BestLast }

public sealed record ContextOptions
{
    /// <summary>
    /// Chapter 12's "nothing relevant" signal: with even the best parent's calibrated probability below this, there's
    /// no evidence and the assistant abstains without the model. 0.005 was chosen on dev (Section 14.8).
    /// </summary>
    public double AbstainBelow { get; init; } = 0.005;

    /// <summary>Parents below this probability are dropped; zero keeps the re-ranker's top four.</summary>
    public double MinProbability { get; init; }

    public int MaxBlocks { get; init; } = 4;

    /// <summary>The most evidence tokens (o200k) a prompt may carry; the weakest blocks go first.</summary>
    public int MaxTokens { get; init; } = 2_400;

    public bool MergeAdjacent { get; init; } = true;

    public bool TableRowsAsKeyValues { get; init; } = true;

    public EvidenceOrder Order { get; init; } = EvidenceOrder.BestFirst;
}

/// <summary>The evidence for one answer, its size and what assembling it decided.</summary>
public sealed record AssembledContext(IReadOnlyList<EvidenceBlock> Blocks, int Tokens, double BestProbability, IReadOnlyList<string> Notes)
{
    public bool IsEmpty => Blocks.Count == 0;
}

/// <summary>
/// Turns re-ranked hits into the evidence a prompt carries. It returns nothing when even the best hit's calibrated
/// probability says nothing is relevant; otherwise it renders table rows with their column names, merges parents
/// that touch in one document, drops text that repeats, fits the token budget by dropping the weakest blocks, then
/// orders and numbers what's left.
/// </summary>
public sealed class ContextAssembler(Calibration calibration, Tokenizer tokenizer, ContextOptions? options = null)
{
    public ContextOptions Options { get; } = options ?? new ContextOptions();

    /// <summary>Assembles evidence from hits scored by a cross-encoder, whose scores are logits.</summary>
    public AssembledContext Assemble(IReadOnlyList<Hit> hits)
    {
        var notes = new List<string>();
        var scored = hits.Select(h => (Hit: h, Probability: calibration.Probability(h.Score))).ToList();
        var best = scored.Count == 0 ? 0 : scored.Max(s => s.Probability);
        if (best < Options.AbstainBelow) return new AssembledContext([], 0, best, [$"best parent {best:0.000}, below {Options.AbstainBelow}"]);
        var selected = scored.Where(s => s.Probability >= Options.MinProbability).Take(Options.MaxBlocks).ToList();
        if (selected.Count < scored.Count) notes.Add($"{scored.Count - selected.Count} of {scored.Count} below {Options.MinProbability:0.0#} or over {Options.MaxBlocks}");

        var blocks = selected.Select(s => Block(s.Hit.Chunk, s.Probability)).ToList();
        if (Options.MergeAdjacent) blocks = Merge(blocks, notes);
        blocks = Deduplicate(blocks, notes);
        blocks = Fit(blocks, notes);

        var ordered = Options.Order == EvidenceOrder.BestFirst
            ? blocks.OrderByDescending(b => b.Probability)
            : blocks.OrderBy(b => b.Probability);
        IReadOnlyList<EvidenceBlock> numbered = [.. ordered.Select((b, i) => b with { Id = $"S{i + 1}" })];
        return new AssembledContext(numbered, numbered.Sum(Count), best, notes);
    }

    private int Count(EvidenceBlock block) => block.CountTokens(tokenizer);

    private EvidenceBlock Block(IndexedChunk chunk, double probability)
    {
        var (_, start, end) = chunk.ReturnKey;
        IReadOnlyList<EvidenceSegment> segments = Options.TableRowsAsKeyValues && chunk.Chunk.Kind == "table-rows"
            ? KeyValueRows(chunk.Chunk, chunk.Document.Text) ?? [Verbatim(chunk.Document.Text, start, end)]
            : [Verbatim(chunk.Document.Text, start, end)];
        return new EvidenceBlock("", chunk.Document.Id, chunk.Document.Title, chunk.Chunk.Header, chunk.DocumentType, chunk.EffectiveFrom, segments, probability);
    }

    /// <summary>A range of the document without its surrounding whitespace.</summary>
    public static EvidenceSegment Verbatim(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return new EvidenceSegment(text[start..end], start, end);
    }

    /// <summary>
    /// A table-rows chunk's span holds Markdown rows without their header row, so "| M12 | 85 N·m |" reaches the
    /// model without saying which column is the torque. The chunk's own text carries each row as "column: value"
    /// pairs (Chapter 9); each of those lines maps back to its Markdown row.
    /// </summary>
    private static List<EvidenceSegment>? KeyValueRows(Chunk chunk, string text)
    {
        var rows = new List<(int Start, int End)>();
        for (var at = chunk.Start; at < chunk.End;)
        {
            var end = text.IndexOf('\n', at, chunk.End - at);
            if (end < 0) end = chunk.End;
            if (end > at) rows.Add((at, end));
            at = end + 1;
        }

        var lines = chunk.Text.Split('\n');
        if (rows.Count == 0 || lines.Length < rows.Count) return null;
        var segments = new List<EvidenceSegment>();
        if (lines.Length > rows.Count)
        {
            var caption = string.Join('\n', lines[..^rows.Count]);
            var at = text.LastIndexOf(caption, chunk.Start, StringComparison.Ordinal);
            if (at >= 0) segments.Add(new EvidenceSegment(caption, at, at + caption.Length));
        }

        segments.AddRange(lines[^rows.Count..].Select((line, i) => new EvidenceSegment(line, rows[i].Start, rows[i].End, Verbatim: false)));
        return segments;
    }

    /// <summary>Parents of one document that touch or overlap become one block, in document order.</summary>
    private static List<EvidenceBlock> Merge(List<EvidenceBlock> blocks, List<string> notes)
    {
        var merged = new List<EvidenceBlock>();
        foreach (var group in blocks.GroupBy(b => b.DocumentId))
        {
            EvidenceBlock? current = null;
            foreach (var block in group.OrderBy(b => b.Segments[0].Start))
            {
                if (current is not null && block.Segments[0].Start <= current.Segments[^1].End + 2)
                {
                    var segments = current.Segments.Concat(block.Segments.Where(s => s.Start >= current.Segments[^1].End)).ToList();
                    current = current with { Segments = segments, Probability = Math.Max(current.Probability, block.Probability) };
                    notes.Add($"merged two parents of {block.DocumentId}");
                    continue;
                }

                if (current is not null) merged.Add(current);
                current = block;
            }

            if (current is not null) merged.Add(current);
        }

        return merged;
    }

    /// <summary>A block whose text another block already contains adds nothing but tokens.</summary>
    private static List<EvidenceBlock> Deduplicate(List<EvidenceBlock> blocks, List<string> notes)
    {
        var kept = new List<EvidenceBlock>();
        foreach (var block in blocks.OrderByDescending(b => b.Probability))
        {
            var text = Normalize(block.Text);
            if (kept.Any(k => Normalize(k.Text).Contains(text, StringComparison.Ordinal)))
            {
                notes.Add($"dropped a repeat from {block.DocumentId}");
                continue;
            }

            kept.Add(block);
        }

        return kept;
    }

    private List<EvidenceBlock> Fit(List<EvidenceBlock> blocks, List<string> notes)
    {
        var kept = blocks.OrderByDescending(b => b.Probability).ToList();
        while (kept.Count > 1 && kept.Sum(Count) > Options.MaxTokens)
        {
            notes.Add($"dropped {kept[^1].DocumentId} ({Count(kept[^1])} tokens) for the budget");
            kept.RemoveAt(kept.Count - 1);
        }

        // One block over the budget keeps its first segments, cut at a token boundary if it must be.
        if (kept.Count == 1 && Count(kept[0]) > Options.MaxTokens)
        {
            var block = kept[0];
            var room = Options.MaxTokens - (Count(block) - tokenizer.CountTokens(block.Text));
            var segments = new List<EvidenceSegment>();
            foreach (var segment in block.Segments)
            {
                var cost = tokenizer.CountTokens(segment.Text) + 1;
                if (cost > room)
                {
                    if (segments.Count == 0 || room > 20)
                    {
                        var cut = tokenizer.GetIndexByTokenCount(segment.Text, Math.Max(room, 1), out _, out _);
                        segments.Add(segment with { Text = segment.Text[..cut], End = segment.Verbatim ? segment.Start + cut : segment.End });
                    }

                    break;
                }

                segments.Add(segment);
                room -= cost;
            }

            notes.Add($"cut {block.DocumentId} to {Options.MaxTokens} tokens");
            kept[0] = block with { Segments = segments };
        }

        return kept;
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
