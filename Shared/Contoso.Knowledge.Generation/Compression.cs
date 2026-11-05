using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Retrieval;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Generation;

/// <summary>
/// Query-focused extractive compression. The cross-encoder scores every sentence and table row of the evidence against
/// the question, and the best are kept, in document order, until the evidence fits a smaller budget. Kept text is
/// copied, never rewritten, so quotes from it can still be verified and every sentence keeps its span.
/// </summary>
public sealed class ExtractiveCompressor(OnnxCrossEncoder model, Tokenizer tokenizer, int maxTokens = 600)
{
    public int MaxTokens => maxTokens;

    public AssembledContext Compress(string question, AssembledContext context)
    {
        var units = context.Blocks
            .SelectMany((block, b) => block.Segments.SelectMany(Units).Select(unit => (Block: b, Unit: unit, Tokens: tokenizer.CountTokens(unit.Text))))
            .ToList();
        if (units.Count == 0) return context;
        var scores = model.Score(question, [.. units.Select(u => u.Unit.Text)]);

        // Every block keeps its best sentence, so no source disappears; the rest compete for what's left of the budget,
        // which also pays for each block's title and section.
        var keep = Enumerable.Range(0, units.Count).GroupBy(i => units[i].Block).Select(g => g.MaxBy(i => scores[i])).ToHashSet();
        var tokens = keep.Sum(i => units[i].Tokens) + context.Blocks.Sum(b => b.CountTokens(tokenizer) - tokenizer.CountTokens(b.Text));
        foreach (var i in Enumerable.Range(0, units.Count).OrderByDescending(i => scores[i]))
        {
            if (keep.Contains(i) || tokens + units[i].Tokens > maxTokens) continue;
            keep.Add(i);
            tokens += units[i].Tokens;
        }

        IReadOnlyList<EvidenceBlock> blocks = [.. context.Blocks.Select((block, b) => block with
        {
            Segments = [.. Enumerable.Range(0, units.Count).Where(i => units[i].Block == b && keep.Contains(i)).Select(i => units[i].Unit)],
        })];
        var total = blocks.Sum(b => b.CountTokens(tokenizer));
        return context with { Blocks = blocks, Tokens = total, Notes = [.. context.Notes, $"compressed from {context.Tokens} to {total} tokens"] };
    }

    /// <summary>A verbatim segment splits into sentences and lines, each with its own span; a rendered row stays whole.</summary>
    private static IEnumerable<EvidenceSegment> Units(EvidenceSegment segment) => segment.Verbatim
        ? Sentences.Split(segment.Text).Select(s => new EvidenceSegment(segment.Text[s.Start..s.End], segment.Start + s.Start, segment.Start + s.End))
        : [segment];
}
