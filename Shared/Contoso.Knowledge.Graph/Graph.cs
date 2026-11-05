using System.Text.Json;

namespace Contoso.Knowledge.Graph;

/// <summary>
/// Where a node or an edge was found: a span of one version of a document (the chunk it was read from), the method
/// that found it and how sure that method is. Every fact in the graph has at least one.
/// </summary>
public sealed record Mention(string DocumentId, string Version, int Start, int End, string Method, double Confidence);

/// <summary>An entity with a canonical ID such as <c>part:PN-4471-B</c> or <c>supplier:fabrikam-seals</c>.</summary>
public sealed class Node(string id, string type, string name)
{
    public string Id { get; } = id;

    public string Type { get; } = type;

    public string Name { get; set; } = name;

    public List<Mention> Mentions { get; init; } = [];
}

public sealed record EdgeKey(string From, string Relation, string To);

/// <summary>A typed, directed relationship and every mention that supports it.</summary>
public sealed class Edge(EdgeKey key)
{
    public EdgeKey Key { get; } = key;

    public List<Mention> Mentions { get; init; } = [];

    /// <summary>Independent mentions add up: one minus the chance that every one of them is wrong.</summary>
    public double Confidence => 1 - Mentions.Aggregate(1.0, (p, m) => p * (1 - m.Confidence));
}

/// <summary>
/// The graph in memory. Nodes and edges carry their mentions, so deleting a document version removes its mentions,
/// then every edge left without one, then every node left without a mention or an edge.
/// </summary>
public sealed class KnowledgeGraph
{
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<EdgeKey, Edge> _edges = [];
    private readonly Dictionary<string, List<Edge>> _adjacent = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Node> Nodes => _nodes.Values;

    public IReadOnlyCollection<Edge> Edges => _edges.Values;

    public Node? Find(string id) => _nodes.GetValueOrDefault(id);

    public Node AddNode(string id, string type, string name, Mention mention)
    {
        if (!_nodes.TryGetValue(id, out var node)) _nodes[id] = node = new Node(id, type, name);
        if (!node.Mentions.Contains(mention)) node.Mentions.Add(mention);
        return node;
    }

    public Edge AddEdge(string from, string relation, string to, Mention mention)
    {
        var key = new EdgeKey(from, relation, to);
        if (!_edges.TryGetValue(key, out var edge))
        {
            _edges[key] = edge = new Edge(key);
            Adjacent(from).Add(edge);
            Adjacent(to).Add(edge);
        }

        if (!edge.Mentions.Contains(mention)) edge.Mentions.Add(mention);
        return edge;
    }

    /// <summary>Edges in either direction, as one undirected neighborhood.</summary>
    public IReadOnlyList<Edge> EdgesOf(string id) => _adjacent.TryGetValue(id, out var edges) ? edges : [];

    /// <summary>
    /// Cascading delete of one document version: its mentions go, then edges with no mention left, then nodes with
    /// neither. Returns what was removed, for the audit log.
    /// </summary>
    public (int Mentions, int Edges, int Nodes) RemoveVersion(string documentId, string version)
    {
        bool Gone(Mention m) => m.DocumentId == documentId && m.Version == version;
        var mentions = _edges.Values.Sum(e => e.Mentions.RemoveAll(Gone)) + _nodes.Values.Sum(n => n.Mentions.RemoveAll(Gone));
        var edges = _edges.Values.Where(e => e.Mentions.Count == 0).ToList();
        foreach (var e in edges)
        {
            _edges.Remove(e.Key);
            _adjacent[e.Key.From].Remove(e);
            _adjacent[e.Key.To].Remove(e);
        }

        var nodes = _nodes.Values.Where(n => n.Mentions.Count == 0 && EdgesOf(n.Id).Count == 0).ToList();
        foreach (var n in nodes)
        {
            _nodes.Remove(n.Id);
            _adjacent.Remove(n.Id);
        }

        return (mentions, edges.Count, nodes.Count);
    }

    /// <summary>The part of the graph a reader may see: only mentions from documents they may read, and what they support.</summary>
    public KnowledgeGraph View(Func<Mention, bool> readable)
    {
        var view = new KnowledgeGraph();
        foreach (var n in _nodes.Values)
        {
            foreach (var m in n.Mentions.Where(readable)) view.AddNode(n.Id, n.Type, n.Name, m);
        }

        foreach (var e in _edges.Values)
        {
            foreach (var m in e.Mentions.Where(readable))
            {
                if (view.Find(e.Key.From) is null || view.Find(e.Key.To) is null) continue;
                view.AddEdge(e.Key.From, e.Key.Relation, e.Key.To, m);
            }
        }

        return view;
    }

    private List<Edge> Adjacent(string id) => _adjacent.TryGetValue(id, out var list) ? list : _adjacent[id] = [];

    private sealed record Stored(List<StoredNode> Nodes, List<StoredEdge> Edges);

    private sealed record StoredNode(string Id, string Type, string Name, List<Mention> Mentions);

    private sealed record StoredEdge(string From, string Relation, string To, List<Mention> Mentions);

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new Stored(
        [.. _nodes.Values.Select(n => new StoredNode(n.Id, n.Type, n.Name, n.Mentions))],
        [.. _edges.Values.Select(e => new StoredEdge(e.Key.From, e.Key.Relation, e.Key.To, e.Mentions))])));

    public static KnowledgeGraph Load(string path)
    {
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path))!;
        var graph = new KnowledgeGraph();
        foreach (var n in stored.Nodes)
        {
            foreach (var m in n.Mentions) graph.AddNode(n.Id, n.Type, n.Name, m);
        }

        foreach (var e in stored.Edges)
        {
            foreach (var m in e.Mentions) graph.AddEdge(e.From, e.Relation, e.To, m);
        }

        return graph;
    }
}
