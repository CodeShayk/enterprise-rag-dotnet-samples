using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;

namespace Contoso.Knowledge.Correction;

/// <summary>The evidence a correction produced, the query it searched with and what it cost.</summary>
public sealed record CorrectionResult(string Strategy, string Query, AssembledContext Context, double Milliseconds, int ModelCalls);

/// <summary>One way to get better evidence when the grader doesn't trust what retrieval returned.</summary>
public interface ICorrectionStrategy
{
    string Name { get; }

    Task<CorrectionResult> CorrectAsync(string question, Person asker, CancellationToken cancellationToken = default);
}

/// <summary>
/// Widen the search: eight parents instead of four, then the best sentences within a larger budget. The compressor
/// is CRAG's knowledge-strip refinement: it cuts the evidence into sentences, scores each against the question with
/// the cross-encoder and keeps the best.
/// </summary>
public sealed class WidenEvidence(IRetriever retriever, ContextAssembler assembler, ExtractiveCompressor compressor, int top = 8) : ICorrectionStrategy
{
    public string Name => $"widen to {top} parents";

    public async Task<CorrectionResult> CorrectAsync(string question, Person asker, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var result = await retriever.RetrieveAsync(new RetrievalRequest(question, asker, top), cancellationToken);
        var context = assembler.Assemble(result.Hits);
        if (!context.IsEmpty) context = compressor.Compress(question, context);
        return new CorrectionResult(Name, question, context, clock.Elapsed.TotalMilliseconds, 0);
    }
}

/// <summary>
/// Rewrite and retry: Chapter 13's rewriter turns the question into a search query, and retrieval runs again with it.
/// The answer is still written for the question the user asked.
/// </summary>
public sealed class RewriteAndRetry(IRetriever retriever, ContextAssembler assembler, ExtractiveCompressor compressor, Rewriter rewriter, LlmCache cache, int top = 4) : ICorrectionStrategy
{
    public string Name => "rewrite the query";

    public async Task<CorrectionResult> CorrectAsync(string question, Person asker, CancellationToken cancellationToken = default)
    {
        var (clock, recorded) = (Stopwatch.StartNew(), cache.Milliseconds);
        var query = await rewriter.RewriteAsync(question, cancellationToken);
        var (rewriteWall, rewriteMs) = (clock.Elapsed.TotalMilliseconds, cache.Milliseconds - recorded);
        var result = await retriever.RetrieveAsync(new RetrievalRequest(query, asker, top), cancellationToken);
        var context = assembler.Assemble(result.Hits);
        if (!context.IsEmpty) context = compressor.Compress(question, context);
        return new CorrectionResult(Name, query, context, clock.Elapsed.TotalMilliseconds - rewriteWall + rewriteMs, 1);
    }
}

/// <summary>
/// Decompose and retrieve: a question that asks for two things searches for each, and the evidence keeps the best
/// parents of both. Chapter 13 measured it on compound questions only.
/// </summary>
public sealed class DecomposeAndRetrieve(IRetriever retriever, ContextAssembler assembler, ExtractiveCompressor compressor, Rewriter rewriter, LlmCache cache, int top = 4) : ICorrectionStrategy
{
    public string Name => "decompose and retrieve each part";

    public async Task<CorrectionResult> CorrectAsync(string question, Person asker, CancellationToken cancellationToken = default)
    {
        var (clock, recorded) = (Stopwatch.StartNew(), cache.Milliseconds);
        var parts = await rewriter.DecomposeAsync(question, cancellationToken);
        var (splitWall, splitMs) = (clock.Elapsed.TotalMilliseconds, cache.Milliseconds - recorded);
        var hits = new List<Hit>();
        foreach (var part in parts)
        {
            var result = await retriever.RetrieveAsync(new RetrievalRequest(part, asker, top), cancellationToken);
            hits.AddRange(result.Hits.Where(h => hits.All(seen => seen.Chunk.ReturnKey != h.Chunk.ReturnKey)));
        }

        var context = assembler.Assemble([.. hits.OrderByDescending(h => h.Score)]);
        if (!context.IsEmpty) context = compressor.Compress(question, context);
        return new CorrectionResult(Name, string.Join(" | ", parts), context, clock.Elapsed.TotalMilliseconds - splitWall + splitMs, 1);
    }
}
