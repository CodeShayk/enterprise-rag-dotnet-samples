using System.Numerics.Tensors;
using Contoso.Knowledge.Query;

namespace Contoso.Knowledge.Performance;

/// <summary>
/// Values looked up by what a question means: the most similar cached question in the same scope, if the cosine of
/// their embeddings reaches the threshold and, with the guard on, both name the same identifiers. Vectors must be unit
/// length, so a dot product is the cosine.
/// </summary>
public sealed class SemanticCache<T>(double threshold, bool guardIdentifiers = true)
{
    private sealed record Entry(string Scope, string Question, float[] Vector, IReadOnlySet<string> Identifiers, T Value);

    private readonly List<Entry> _entries = [];

    public int Count => _entries.Count;

    public void Add(string scope, string question, ReadOnlySpan<float> vector, T value) =>
        _entries.Add(new Entry(scope, question, vector.ToArray(), IdentifiersOf(question), value));

    /// <summary>The nearest cached question in the scope and its similarity, whether or not it's a hit.</summary>
    public (T Value, string Question, double Similarity, bool Hit)? Find(string scope, string question, ReadOnlySpan<float> vector)
    {
        Entry? best = null;
        var similarity = double.MinValue;
        foreach (var entry in _entries)
        {
            if (entry.Scope != scope) continue;
            var s = TensorPrimitives.Dot(entry.Vector, vector);
            if (s > similarity) (best, similarity) = (entry, s);
        }

        if (best is null) return null;
        var hit = similarity >= threshold && (!guardIdentifiers || best.Identifiers.SetEquals(IdentifiersOf(question)));
        return (best.Value, best.Question, similarity, hit);
    }

    /// <summary>Identifiers compared without case: PN-4471-C, P-201, M12, E-217.</summary>
    public static IReadOnlySet<string> IdentifiersOf(string question) =>
        Identifiers.Find(question).Select(i => i.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
}
