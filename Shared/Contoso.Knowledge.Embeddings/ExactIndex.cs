using System.Numerics.Tensors;

namespace Contoso.Knowledge.Embeddings;

/// <summary>
/// The exact-search baseline: every vector is scored against the query and the best k are kept. It costs one dot
/// product per vector per query, so it slows down linearly with the collection, and it is the reference that every
/// approximate index and every compressed vector is measured against. Vectors must be L2-normalized, so that the dot
/// product is the cosine similarity.
/// </summary>
public sealed class ExactIndex(IReadOnlyList<float[]> vectors)
{
    public int Count => vectors.Count;

    public IReadOnlyList<(int Index, float Score)> Search(ReadOnlySpan<float> query, int k, Func<int, bool>? filter = null)
    {
        var best = new PriorityQueue<int, float>(k + 1);
        for (var i = 0; i < vectors.Count; i++)
        {
            if (filter is not null && !filter(i)) continue;
            var score = TensorPrimitives.Dot(query, vectors[i]);
            if (best.Count < k) best.Enqueue(i, score);
            else if (score > PeekScore(best)) best.EnqueueDequeue(i, score);
        }

        var results = new List<(int, float)>(best.Count);
        while (best.TryDequeue(out var index, out var score)) results.Add((index, score));
        results.Reverse();
        return results;
    }

    private static float PeekScore(PriorityQueue<int, float> queue) => queue.TryPeek(out _, out var score) ? score : float.MinValue;
}
