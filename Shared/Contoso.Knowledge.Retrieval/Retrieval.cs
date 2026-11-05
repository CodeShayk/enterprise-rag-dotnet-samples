using Contoso.Corpus;
using Contoso.Knowledge.Chunking;

namespace Contoso.Knowledge.Retrieval;

/// <summary>
/// The metadata a search may be restricted to, on top of the asker's permissions (Section 11.5). Null means no
/// restriction. Chapter 13 reads these from the question; Chapter 22 owns the permission side.
/// </summary>
public sealed record RetrievalFilter
{
    /// <summary>manuals, procedures, records, notices and reports, policies and bulletins.</summary>
    public IReadOnlyList<string>? Collections { get; init; }

    public IReadOnlyList<string>? DocumentTypes { get; init; }

    /// <summary>Equipment tags to match; include <see cref="Untagged"/> to let documents without tags through (Chapter 13).</summary>
    public IReadOnlyList<string>? EquipmentTags { get; init; }

    /// <summary>The tag value that stands for "this document has no equipment tags": most of Contoso's documents have none.</summary>
    public const string Untagged = "(untagged)";

    /// <summary>Only versions in effect on this day (Chapter 6's effective windows).</summary>
    public DateOnly? AsOf { get; init; }
}

/// <summary>One search: the question, who asks, how many results and what they may be restricted to.</summary>
public sealed record RetrievalRequest(string Question, Person Asker, int Top, RetrievalFilter? Filter = null);

/// <summary>A chunk a retriever found, the score that retriever gave it and the retriever's name.</summary>
public sealed record Hit(IndexedChunk Chunk, double Score, string Source);

/// <summary>
/// Ranked hits, one per return span, and the sources that failed or ran out of time: a hybrid search that lost one
/// side says so instead of quietly returning half of its evidence.
/// </summary>
public sealed record RetrievalResult(IReadOnlyList<Hit> Hits, IReadOnlyList<string> Degraded)
{
    public static RetrievalResult Of(IReadOnlyList<Hit> hits) => new(hits, []);
}

/// <summary>
/// Chapter 4's retriever port in the shape hybrid retrieval needs (ADR-0006): the request carries filters and the
/// result reports degradation. Contract: every hit is readable by the asker and passes the filter before ranking,
/// so the top k are the asker's top k.
/// </summary>
public interface IRetriever
{
    string Name { get; }

    Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A chunk with the metadata that permissions and filters apply to.</summary>
public sealed record IndexedChunk(
    int Ordinal, Chunk Chunk, SourceDocument Document, string Collection, string DocumentType,
    IReadOnlyList<string> EquipmentTags, DateOnly? EffectiveFrom, DateOnly? EffectiveTo)
{
    /// <summary>The span handed to the model: a parent counts once however many of its children match.</summary>
    public (string Doc, int Start, int End) ReturnKey => (Chunk.Doc, Chunk.ReturnStart, Chunk.ReturnEnd);

    public bool Passes(Person asker, RetrievalFilter? filter) => asker.CanRead(Document) && (filter is null || Matches(filter));

    public bool Matches(RetrievalFilter filter) =>
        (filter.Collections is null || filter.Collections.Contains(Collection)) &&
        (filter.DocumentTypes is null || filter.DocumentTypes.Contains(DocumentType)) &&
        (filter.EquipmentTags is null || filter.EquipmentTags.Any(EquipmentTags.Contains) || (EquipmentTags.Count == 0 && filter.EquipmentTags.Contains(RetrievalFilter.Untagged))) &&
        (filter.AsOf is not { } day || ((EffectiveFrom is null || EffectiveFrom <= day) && (EffectiveTo is null || day <= EffectiveTo)));
}

public static class Ranking
{
    /// <summary>The best hit for each return span, in rank order, at most <paramref name="top"/> of them.</summary>
    public static IReadOnlyList<Hit> DistinctReturns(IEnumerable<Hit> ranked, int top)
    {
        var seen = new HashSet<(string, int, int)>();
        var hits = new List<Hit>(top);
        foreach (var hit in ranked)
        {
            if (!seen.Add(hit.Chunk.ReturnKey)) continue;
            hits.Add(hit);
            if (hits.Count == top) break;
        }

        return hits;
    }
}
