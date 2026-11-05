using Contoso.Knowledge.Retrieval;

namespace Contoso.Knowledge.Graph;

/// <summary>
/// Retrieval over the graph: the question's entities linked to nodes, their k-hop neighborhood, the shortest paths
/// between them and personalized PageRank from them, which ranks what is near the question's entities over what is
/// merely well connected.
/// </summary>
public sealed class GraphSearch(KnowledgeGraph graph, EntityRecognizer recognizer)
{
    public KnowledgeGraph Graph => graph;

    /// <summary>Entity linking: the question's identifiers and known names that are nodes of this graph.</summary>
    public IReadOnlyList<string> Link(string question) =>
        [.. recognizer.Find(question).Select(e => e.Id).Distinct().Where(id => graph.Find(id) is not null)];

    public static string Other(Edge edge, string node) => edge.Key.From == node ? edge.Key.To : edge.Key.From;

    /// <summary>The edges within <paramref name="hops"/> of the seeds, breadth first.</summary>
    public IReadOnlyList<Edge> Neighborhood(IEnumerable<string> seeds, int hops)
    {
        var (seen, frontier, edges) = (new HashSet<string>(seeds), new List<string>(seeds), new HashSet<Edge>());
        for (var depth = 0; depth < hops; depth++)
        {
            var next = new List<string>();
            foreach (var node in frontier)
            {
                foreach (var edge in graph.EdgesOf(node))
                {
                    edges.Add(edge);
                    if (seen.Add(Other(edge, node))) next.Add(Other(edge, node));
                }
            }

            frontier = next;
        }

        return [.. edges];
    }

    /// <summary>Every shortest path between two nodes of at most <paramref name="maxHops"/> edges, ignoring direction.</summary>
    public IReadOnlyList<IReadOnlyList<Edge>> Paths(string from, string to, int maxHops = 4)
    {
        var paths = new List<IReadOnlyList<Edge>>();
        var layer = new List<(string Node, List<Edge> Path)> { (from, []) };
        var visited = new HashSet<string> { from };
        for (var depth = 0; depth < maxHops && paths.Count == 0 && layer.Count > 0; depth++)
        {
            var next = new List<(string, List<Edge>)>();
            var reached = new HashSet<string>();
            foreach (var (node, path) in layer)
            {
                foreach (var edge in graph.EdgesOf(node))
                {
                    var other = Other(edge, node);
                    if (visited.Contains(other)) continue;
                    if (other == to) paths.Add([.. path, edge]);
                    else
                    {
                        next.Add((other, [.. path, edge]));
                        reached.Add(other);
                    }
                }
            }

            visited.UnionWith(reached);
            layer = next;
        }

        return paths;
    }

    /// <summary>
    /// Personalized PageRank: a walk that follows edges and, with probability <paramref name="restart"/>, jumps back
    /// to a seed. Scores concentrate around the question's entities.
    /// </summary>
    public IReadOnlyDictionary<string, double> PersonalizedPageRank(IReadOnlyCollection<string> seeds, double restart = 0.15, int iterations = 30) =>
        PersonalizedPageRank(seeds.ToDictionary(s => s, _ => 1.0), restart, iterations);

    /// <summary>The same walk with a weight per seed: the question's own entities count more than ones found on the way.</summary>
    public IReadOnlyDictionary<string, double> PersonalizedPageRank(IReadOnlyDictionary<string, double> seeds, double restart = 0.15, int iterations = 30)
    {
        var total = seeds.Values.Sum();
        var start = seeds.ToDictionary(s => s.Key, s => s.Value / total);
        var nodes = graph.Nodes.Select(n => n.Id).ToList();
        var rank = nodes.ToDictionary(n => n, n => start.GetValueOrDefault(n));
        for (var i = 0; i < iterations; i++)
        {
            var next = nodes.ToDictionary(n => n, n => restart * start.GetValueOrDefault(n));
            foreach (var n in nodes)
            {
                var edges = graph.EdgesOf(n);
                if (edges.Count == 0)
                {
                    foreach (var (s, w) in start) next[s] += (1 - restart) * rank[n] * w;
                    continue;
                }

                foreach (var e in edges) next[Other(e, n)] += (1 - restart) * rank[n] / edges.Count;
            }

            rank = next;
        }

        return rank;
    }

    /// <summary>A subgraph as lines the model can read, each edge with the documents that state it.</summary>
    public string Serialize(IEnumerable<Edge> edges) => string.Join('\n', edges.Select(e =>
        $"{graph.Find(e.Key.From)?.Name} -[{e.Key.Relation}]-> {graph.Find(e.Key.To)?.Name} ({string.Join(", ", e.Mentions.Select(m => m.DocumentId).Distinct())})"));
}

/// <summary>
/// The graph as a retriever. Link the question's entities; with two rounds, read the passages that mention them and
/// add the entities those passages name as weaker seeds (graph, then passages, then graph again). Take the paths
/// between the question's entities and the neighborhoods of all seeds; rank the edges of the relation the question
/// asks for first, then the paths, then the rest, each by the personalized PageRank of their ends; and return the
/// chunks the edges were read from, with the passages that named the new seeds after the paths. The graph is viewed
/// through the asker's permissions before anything is ranked.
/// </summary>
public sealed class GraphRetriever(KnowledgeGraph graph, EntityRecognizer recognizer, IReadOnlyList<IndexedChunk> chunks, int rounds = 2, int hops = 2, int passages = 6) : IRetriever
{
    public string Name => rounds > 1 ? "graph" : "graph, one round";

    /// <summary>The edges this retriever chose for the last request, in rank order, for tracing and serialization.</summary>
    public IReadOnlyList<Edge> LastEdges { get; private set; } = [];

    /// <summary>The entities the last request found in passages rather than in the question.</summary>
    public IReadOnlyList<string> LastDiscovered { get; private set; } = [];

    public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var readable = chunks.Where(c => request.Asker.CanRead(c.Document)).ToList();
        var documents = readable.Select(c => c.Document.Id).ToHashSet();
        var search = new GraphSearch(graph.View(m => documents.Contains(m.DocumentId)), recognizer);
        var seeds = search.Link(request.Question);
        (LastEdges, LastDiscovered) = ([], []);
        if (seeds.Count == 0) return Task.FromResult(RetrievalResult.Of([]));

        IndexedChunk? ChunkOf(Mention m) => readable.FirstOrDefault(c => c.Document.Id == m.DocumentId && c.Chunk.Start < m.End && m.Start < c.Chunk.End);
        var weights = seeds.ToDictionary(s => s, _ => 1.0);
        var named = new List<IndexedChunk>();
        if (rounds > 1)
        {
            named = [.. seeds.SelectMany(s => search.Graph.Find(s)!.Mentions).Select(ChunkOf).OfType<IndexedChunk>().DistinctBy(c => c.ReturnKey).Take(passages)];
            LastDiscovered = [.. named.SelectMany(c => recognizer.Find(c.Chunk.Text)).Select(e => e.Id).Distinct().Where(id => !weights.ContainsKey(id) && search.Graph.Find(id) is not null)];
            foreach (var id in LastDiscovered) weights[id] = 0.3;
        }

        // Edges of the relation the question asks for come first, then the paths between its entities, then the rest,
        // each group by the personalized PageRank of the edge's ends.
        var rank = search.PersonalizedPageRank(weights);
        var cued = Schema.CuedBy(request.Question);
        var paths = seeds.SelectMany((a, i) => seeds.Skip(i + 1).SelectMany(b => search.Paths(a, b))).SelectMany(p => p).Distinct().ToList();
        var near = search.Neighborhood(seeds, hops).Concat(search.Neighborhood(LastDiscovered, hops)).Distinct().Except(paths).ToList();
        double Score(Edge e) => rank[e.Key.From] + rank[e.Key.To];
        var asked = paths.Concat(near).Where(e => cued.Contains(e.Key.Relation)).OrderByDescending(Score).ToList();
        LastEdges = [.. asked.Concat(paths.Except(asked)).Concat(near.Except(asked).OrderByDescending(Score))];

        var hits = new List<Hit>();
        void Add(IndexedChunk? chunk)
        {
            if (chunk is not null) hits.Add(new Hit(chunk, 1.0 / (hits.Count + 1), Name));
        }

        foreach (var m in LastEdges.Take(asked.Count + paths.Except(asked).Count()).SelectMany(e => e.Mentions.OrderByDescending(m => m.Confidence))) Add(ChunkOf(m));
        foreach (var c in named) Add(c);
        foreach (var m in LastEdges.Skip(asked.Count + paths.Except(asked).Count()).SelectMany(e => e.Mentions.OrderByDescending(m => m.Confidence))) Add(ChunkOf(m));
        return Task.FromResult(RetrievalResult.Of(Ranking.DistinctReturns(hits, request.Top)));
    }
}
