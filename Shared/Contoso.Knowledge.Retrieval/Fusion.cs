namespace Contoso.Knowledge.Retrieval;

/// <summary>Combines several rankings of the same request into one.</summary>
public interface IFusion
{
    string Name { get; }

    IReadOnlyList<Hit> Fuse(IReadOnlyList<IReadOnlyList<Hit>> rankings, int top);
}

/// <summary>
/// Reciprocal Rank Fusion (Cormack, Clarke and Büttcher, 2009): each ranking adds weight / (k + rank) for every
/// return span it holds. Only ranks count, so scores on different scales never meet. A larger k flattens the
/// difference between the first places.
/// </summary>
public sealed class ReciprocalRankFusion(int k = 60, IReadOnlyList<double>? weights = null) : IFusion
{
    public string Name => weights is null ? $"RRF, k = {k}" : $"RRF, k = {k}, weights {string.Join(" : ", weights)}";

    public IReadOnlyList<Hit> Fuse(IReadOnlyList<IReadOnlyList<Hit>> rankings, int top)
    {
        var fused = new Dictionary<(string, int, int), (Hit Hit, double Score)>();
        for (var r = 0; r < rankings.Count; r++)
        {
            for (var rank = 1; rank <= rankings[r].Count; rank++)
            {
                var hit = rankings[r][rank - 1];
                var add = (weights?[r] ?? 1.0) / (k + rank);
                fused[hit.Chunk.ReturnKey] = fused.TryGetValue(hit.Chunk.ReturnKey, out var f) ? (f.Hit, f.Score + add) : (hit, add);
            }
        }

        return [.. fused.Values.OrderByDescending(f => f.Score).Take(top).Select(f => f.Hit with { Score = f.Score, Source = Name })];
    }
}

public enum Normalization
{
    MinMax,
    ZScore,
}

/// <summary>
/// A convex combination of normalized scores: alpha times the first ranking's score plus (1 - alpha) times the
/// second's. A span missing from a ranking gets that ranking's lowest normalized score.
/// </summary>
public sealed class ConvexFusion(double alpha, Normalization normalization = Normalization.MinMax) : IFusion
{
    public string Name => $"convex, alpha = {alpha:0.0#}, {(normalization == Normalization.MinMax ? "min-max" : "z-score")}";

    public IReadOnlyList<Hit> Fuse(IReadOnlyList<IReadOnlyList<Hit>> rankings, int top)
    {
        if (rankings.Count != 2) throw new ArgumentException("A convex combination fuses two rankings.", nameof(rankings));
        var normalized = rankings.Select(Normalize).ToList();
        var keys = rankings.SelectMany(r => r).GroupBy(h => h.Chunk.ReturnKey).Select(g => g.First()).ToList();
        return [.. keys
            .Select(hit => (hit, score: alpha * Lookup(normalized[0], hit) + (1 - alpha) * Lookup(normalized[1], hit)))
            .OrderByDescending(s => s.score)
            .Take(top)
            .Select(s => s.hit with { Score = s.score, Source = Name })];
    }

    private (Dictionary<(string, int, int), double> Scores, double Floor) Normalize(IReadOnlyList<Hit> ranking)
    {
        if (ranking.Count == 0) return ([], 0);
        var scores = ranking.Select(h => h.Score).ToArray();
        Func<double, double> scale = normalization switch
        {
            Normalization.MinMax when scores.Max() > scores.Min() => s => (s - scores.Min()) / (scores.Max() - scores.Min()),
            Normalization.ZScore when StandardDeviation(scores) > 0 => s => (s - scores.Average()) / StandardDeviation(scores),
            _ => _ => 1,
        };
        var map = new Dictionary<(string, int, int), double>();
        foreach (var hit in ranking) map.TryAdd(hit.Chunk.ReturnKey, scale(hit.Score));
        return (map, map.Values.Min());
    }

    private static double Lookup((Dictionary<(string, int, int), double> Scores, double Floor) ranking, Hit hit) =>
        ranking.Scores.TryGetValue(hit.Chunk.ReturnKey, out var score) ? score : ranking.Floor;

    private static double StandardDeviation(double[] values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Length);
    }
}
