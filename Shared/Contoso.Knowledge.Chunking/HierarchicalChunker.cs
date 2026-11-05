using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Chunking;

/// <summary>Writes the text of a summary node.</summary>
public interface ISummarizer
{
    string Name { get; }

    Task<string> SummarizeAsync(string title, string breadcrumb, string text, CancellationToken cancellationToken = default);
}

/// <summary>A summary for free: the first sentence of each paragraph or list, up to a token budget.</summary>
public sealed class LeadSentences(Tokenizer tokenizer, int budget = 80) : ISummarizer
{
    public string Name => "lead";

    public Task<string> SummarizeAsync(string title, string breadcrumb, string text, CancellationToken cancellationToken = default)
    {
        var leads = text.Split("\n\n").Select(p => Sentences.Split(p) is { Count: > 0 } s ? p[s[0].Start..s[0].End] : "").Where(l => l.Length > 0);
        var summary = new List<string>();
        var tokens = 0;
        foreach (var lead in leads)
        {
            var count = tokenizer.CountTokens(lead);
            if (summary.Count > 0 && tokens + count > budget) break;
            summary.Add(lead);
            tokens += count;
        }

        return Task.FromResult(string.Join(' ', summary));
    }
}

/// <summary>A summary written by a chat model, for sections worth the cost.</summary>
public sealed class ModelSummarizer(IChatClient chat, string model) : ISummarizer
{
    public string Name => model;

    public async Task<string> SummarizeAsync(string title, string breadcrumb, string text, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            Summarize this section of "{title}" in at most two sentences for a search index. Name the equipment, parts,
            values and actions it covers. Answer with the summary only.

            Section: {breadcrumb}

            {text}
            """;
        var response = await chat.GetResponseAsync(prompt, new ChatOptions { Temperature = 0, MaxOutputTokens = 120 }, cancellationToken);
        return response.Text.Trim();
    }
}

/// <summary>
/// Hierarchical chunking: a tree of document, sections and passages. Passages are indexed as they are. A section
/// longer than one passage also gets a summary node that returns the section, capped at <paramref name="sectionMax"/>
/// tokens, so a question about the section as a whole can find it. This is the two-level, budgeted version of
/// RAPTOR's recursive summary tree.
/// </summary>
public sealed class HierarchicalChunker(Tokenizer tokenizer, ISummarizer summarizer, int passage = 150, int sectionMax = 800) : IChunker
{
    private readonly StructureChunker _passages = new(tokenizer, passage);

    public string Name => $"hierarchical-{summarizer.Name}";

    /// <summary>Summaries written so far, for the CPU budget.</summary>
    public int Summaries { get; private set; }

    public async ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default)
    {
        var text = source.Text;
        var chunks = new List<Chunk>(await _passages.SplitAsync(source, cancellationToken));
        foreach (var section in Structure.Sections(source))
        {
            var (start, end) = (section[0].Start, section[^1].End);
            if (chunks.Count(c => c.Start >= start && c.End <= end) < 2) continue;

            var breadcrumb = source.Breadcrumb(Structure.PathBlock(source, section));
            var summary = await summarizer.SummarizeAsync(source.Title, breadcrumb, text[start..end], cancellationToken);
            Summaries++;
            var returns = (start, Tokens.Fit(tokenizer, text, start, end, sectionMax));
            chunks.Add(new Chunk(source.Id, start, end, summary) { Header = breadcrumb, Kind = "summary", Returns = returns });
        }

        return chunks;
    }
}
