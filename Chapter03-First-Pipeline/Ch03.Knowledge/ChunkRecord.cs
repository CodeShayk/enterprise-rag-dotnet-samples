using System.Linq.Expressions;
using Contoso.AI;
using Contoso.Corpus;
using Microsoft.Extensions.VectorData;

namespace Ch03.Knowledge;

/// <summary>One chunk as the vector store keeps it: its text, where it came from, who may read it and its embedding.</summary>
public sealed class ChunkRecord
{
    // The vector length is part of the collection's schema. A model with a different length
    // needs a new collection (Chapter 10 covers re-embedding and migration).
    public const int Dimensions = HashingEmbeddingGenerator.DefaultDimensions;

    [VectorStoreKey]
    public string Id { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string DocumentId { get; set; } = "";

    [VectorStoreData]
    public string Title { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string Tenant { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string[] AllowedGroups { get; set; } = [];

    [VectorStoreData]
    public string Text { get; set; } = "";

    [VectorStoreVector(Dimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>The permission filter that runs inside the vector search, before anything is ranked.</summary>
public static class AccessFilter
{
    /// <summary>
    /// Builds <c>r =&gt; r.AllowedGroups.Contains(g1) || r.AllowedGroups.Contains(g2) || ...</c> for the
    /// asker's groups. Equality, <c>||</c> and <c>Contains</c> on a list field are the filter shapes
    /// that vector stores translate into their own query languages, so the same filter moves with
    /// the assistant when Chapter 10 replaces the in-memory store.
    /// </summary>
    public static Expression<Func<ChunkRecord, bool>> ReadableBy(Person asker)
    {
        var record = Expression.Parameter(typeof(ChunkRecord), "r");
        var allowed = Expression.Property(record, nameof(ChunkRecord.AllowedGroups));

        var body = asker.Groups.Order(StringComparer.Ordinal)
            .Select(group => (Expression)Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)], allowed, Expression.Constant(group)))
            .Aggregate(Expression.OrElse);

        return Expression.Lambda<Func<ChunkRecord, bool>>(body, record);
    }
}
