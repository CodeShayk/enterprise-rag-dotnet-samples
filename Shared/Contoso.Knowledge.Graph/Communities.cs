namespace Contoso.Knowledge.Graph;

/// <summary>
/// Louvain community detection on the undirected graph, edges weighted by their mention count: nodes move to the
/// neighboring community that raises modularity most, communities are folded into single nodes, and the two steps
/// repeat until nothing moves. Leiden refines the same idea so that no community comes out disconnected.
/// </summary>
public static class Louvain
{
    public static IReadOnlyDictionary<string, int> Detect(KnowledgeGraph graph, int seed = 2027)
    {
        // Weighted adjacency between node indexes.
        var ids = graph.Nodes.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var index = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var weights = new Dictionary<(int, int), double>();
        foreach (var e in graph.Edges)
        {
            var (a, b) = (index[e.Key.From], index[e.Key.To]);
            if (a == b) continue;
            var key = a < b ? (a, b) : (b, a);
            weights[key] = weights.GetValueOrDefault(key) + e.Mentions.Count;
        }

        var membership = Enumerable.Range(0, ids.Count).ToArray();
        var level = Adjacency(ids.Count, weights);
        var random = new Random(seed);
        while (true)
        {
            var community = MoveNodes(level, random);
            if (community.Distinct().Count() == level.Length) break;
            for (var i = 0; i < membership.Length; i++) membership[i] = community[membership[i]];
            var renumber = community.Distinct().Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
            for (var i = 0; i < membership.Length; i++) membership[i] = renumber[membership[i]];
            var folded = new Dictionary<(int, int), double>();
            for (var u = 0; u < level.Length; u++)
            {
                foreach (var (v, w) in level[u])
                {
                    if (v < u) continue;
                    var (cu, cv) = (renumber[community[u]], renumber[community[v]]);
                    var key = cu <= cv ? (cu, cv) : (cv, cu);
                    folded[key] = folded.GetValueOrDefault(key) + w;
                }
            }

            level = Adjacency(renumber.Count, folded);
        }

        return ids.Select((id, i) => (id, membership[i])).ToDictionary(x => x.id, x => x.Item2);
    }

    private static Dictionary<int, double>[] Adjacency(int n, Dictionary<(int, int), double> weights)
    {
        var adjacency = Enumerable.Range(0, n).Select(_ => new Dictionary<int, double>()).ToArray();
        foreach (var ((a, b), w) in weights)
        {
            adjacency[a][b] = adjacency[a].GetValueOrDefault(b) + w;
            if (a != b) adjacency[b][a] = adjacency[b].GetValueOrDefault(a) + w;
        }

        return adjacency;
    }

    /// <summary>Local moving: each node joins the neighboring community with the largest modularity gain, until none moves.</summary>
    private static int[] MoveNodes(Dictionary<int, double>[] adjacency, Random random)
    {
        var n = adjacency.Length;
        var degree = adjacency.Select((a, i) => a.Values.Sum() + a.GetValueOrDefault(i)).ToArray();
        var total = degree.Sum();
        var community = Enumerable.Range(0, n).ToArray();
        var communityDegree = (double[])degree.Clone();
        if (total == 0) return community;
        var moved = true;
        while (moved)
        {
            moved = false;
            foreach (var u in Enumerable.Range(0, n).OrderBy(_ => random.Next()))
            {
                var links = new Dictionary<int, double>();
                foreach (var (v, w) in adjacency[u])
                {
                    if (v != u) links[community[v]] = links.GetValueOrDefault(community[v]) + w;
                }

                var current = community[u];
                communityDegree[current] -= degree[u];
                var (best, bestGain) = (current, links.GetValueOrDefault(current) - communityDegree[current] * degree[u] / total);
                foreach (var (c, w) in links)
                {
                    var gain = w - communityDegree[c] * degree[u] / total;
                    if (gain > bestGain + 1e-12) (best, bestGain) = (c, gain);
                }

                community[u] = best;
                communityDegree[best] += degree[u];
                if (best != current) moved = true;
            }
        }

        return community;
    }

    /// <summary>Newman's modularity of a partition: the share of edge weight inside communities, less what chance would put there.</summary>
    public static double Modularity(KnowledgeGraph graph, IReadOnlyDictionary<string, int> community)
    {
        var edges = graph.Edges.Where(e => e.Key.From != e.Key.To).ToList();
        var m = edges.Sum(e => (double)e.Mentions.Count);
        if (m == 0) return 0;
        var degree = new Dictionary<string, double>();
        foreach (var e in edges)
        {
            degree[e.Key.From] = degree.GetValueOrDefault(e.Key.From) + e.Mentions.Count;
            degree[e.Key.To] = degree.GetValueOrDefault(e.Key.To) + e.Mentions.Count;
        }

        var inside = edges.Where(e => community[e.Key.From] == community[e.Key.To]).Sum(e => (double)e.Mentions.Count) / m;
        var expected = degree.GroupBy(d => community[d.Key]).Sum(g => Math.Pow(g.Sum(d => d.Value) / (2 * m), 2));
        return inside - expected;
    }
}
