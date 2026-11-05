using System.Diagnostics;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ch09.Chunking.Cli;

/// <summary>A strategy's chunks for the whole corpus, with the time it took to make them.</summary>
public sealed record Chunked(string Name, IReadOnlyList<Chunk> Chunks, TimeSpan Elapsed);

/// <summary>Adds context to every chunk an inner strategy makes, for indexing only.</summary>
public sealed class EnrichedChunker(IChunker inner, string suffix, Func<Chunk, Chunk> enrich) : IChunker
{
    public string Name => $"{inner.Name}+{suffix}";

    public async ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default) =>
        [.. (await inner.SplitAsync(source, cancellationToken)).Select(enrich)];
}

/// <summary>The chunking experiment: every strategy, the same questions, retriever and k.</summary>
public static class Experiment
{
    public static async Task<Chunked> ChunkAsync(IChunker chunker, Corpus corpus)
    {
        var stopwatch = Stopwatch.StartNew();
        var chunks = new List<Chunk>();
        foreach (var source in corpus.Sources) chunks.AddRange(await chunker.SplitAsync(source));
        return new Chunked(chunker.Name, chunks, stopwatch.Elapsed);
    }

    /// <summary>Labeled spans that no single chunk returns whole: evidence a boundary cut in two.</summary>
    public static (int Cut, int Total) CutSpans(Chunked chunked, IEnumerable<LocatedSpan> spans)
    {
        var byDoc = chunked.Chunks.GroupBy(c => c.Doc).ToDictionary(g => g.Key, g => g.ToList());
        var all = spans.ToList();
        var cut = all.Count(s => !byDoc.TryGetValue(s.Doc, out var list) || !list.Any(c => c.ReturnStart <= s.Start && c.ReturnEnd >= s.End));
        return (cut, all.Count);
    }

    /// <summary>Tokens of evidence a question hands the model: the retrieved spans, overlaps counted once.</summary>
    public static int EvidenceTokens(IReadOnlyList<ChunkSpan> retrieved, Corpus corpus, Tokenizer tokenizer) =>
        retrieved.GroupBy(r => r.Doc).Sum(doc =>
        {
            var text = corpus.Documents[doc.Key].Text;
            var merged = new List<(int Start, int End)>();
            foreach (var span in doc.OrderBy(s => s.Start))
            {
                if (merged.Count > 0 && span.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
                else merged.Add((span.Start, span.End));
            }

            return merged.Sum(m => tokenizer.CountTokens(text.AsSpan(m.Start, m.End - m.Start)));
        });

    /// <summary>
    /// One strategy per collection, chosen on dev questions. A strategy that is clearly worse than the baseline over
    /// all collections can't be chosen for one, because a collection's few questions can favor it by chance. Among
    /// the rest, a collection takes the highest sufficiency on its questions, then the fewest evidence tokens.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Choose(
        IReadOnlyList<(Chunked Chunked, RunRecord Dev)> strategies, string baseline, IReadOnlyDictionary<string, string> collectionOf,
        Corpus corpus, Tokenizer tokenizer)
    {
        var reference = Sufficiency(strategies.Single(s => s.Chunked.Name == baseline).Dev);
        return strategies
            .Where(s => Bootstrap.Paired(reference, Sufficiency(s.Dev)).High >= 0)
            .SelectMany(s => s.Dev.Answerable.Select(q =>
                (Collection: collectionOf[q.Id], s.Chunked.Name, q.Sufficient, Tokens: EvidenceTokens(q.Retrieved, corpus, tokenizer))))
            .GroupBy(r => r.Collection)
            .ToDictionary(g => g.Key, g => g.GroupBy(r => r.Name)
                .OrderByDescending(s => s.Average(r => r.Sufficient)).ThenBy(s => s.Average(r => r.Tokens)).First().Key);

        static double[] Sufficiency(RunRecord run) => [.. run.Answerable.Select(q => q.Sufficient)];
    }

    /// <summary>Model-written context for some chunks, as `enrich` saved it: one JSON object per line.</summary>
    public sealed record ContextLine(string Doc, int Start, int End, string Context, double Seconds);

    public static string ContextPath { get; } = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "runs", "ch09", "context-llama3.2.jsonl");

    public static IReadOnlyDictionary<(string, int, int), string> LoadContext() =>
        File.Exists(ContextPath)
            ? File.ReadLines(ContextPath).Select(l => System.Text.Json.JsonSerializer.Deserialize<ContextLine>(l, System.Text.Json.JsonSerializerOptions.Web)!)
                .GroupBy(c => (c.Doc, c.Start, c.End)).ToDictionary(g => g.Key, g => g.Last().Context)
            : new Dictionary<(string, int, int), string>();

    /// <summary>
    /// The embedding model for semantic chunking: Apache-2.0, small enough for a CPU, and strong on similarity and
    /// clustering. It expects a one-sentence task instruction in front of text it compares.
    /// </summary>
    public const string EmbeddingModel = "qwen3-embedding:0.6b";

    public const string Instruction = "Instruct: Identify the topic or theme of the given text\nQuery:";

    public static IEnumerable<IChunker> Strategies(Tokenizer tokenizer, IEmbeddingGenerator<string, Embedding<float>>? embedder) =>
    [
        new FixedSizeChunker(tokenizer, 100, 15),
        new FixedSizeChunker(tokenizer, 200, 30),
        new FixedSizeChunker(tokenizer, 400, 60),
        new RecursiveChunker(tokenizer, 200),
        new StructureChunker(tokenizer, 300, breadcrumbs: false),
        new StructureChunker(tokenizer, 300),
        new EnrichedChunker(new StructureChunker(tokenizer, 300), "entities", Enrichment.WithEntities),
        .. LoadContext() is { Count: > 0 } context
            ? new IChunker[] { new EnrichedChunker(new StructureChunker(tokenizer, 300), "context", c => context.TryGetValue((c.Doc, c.Start, c.End), out var text) ? c with { Header = $"{c.Header}\n{text}" } : c) }
            : [],
        .. embedder is null ? [] : new IChunker[]
        {
            new SemanticChunker(embedder, tokenizer, 0.9, 300, prefix: Instruction),
            new SectionSemanticChunker(embedder, tokenizer, 300, prefix: Instruction),
        },
        new HierarchicalChunker(tokenizer, new LeadSentences(tokenizer)),
        new ParentChildChunker(tokenizer, 100, 600),
        new ParentChildChunker(tokenizer, window: 2),
    ];
}
