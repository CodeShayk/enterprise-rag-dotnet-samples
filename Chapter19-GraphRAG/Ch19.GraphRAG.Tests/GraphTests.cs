using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Graph;
using DuckDB.NET.Data;
using Microsoft.Extensions.AI;

namespace Ch19.GraphRAG.Tests;

public sealed class ExtractionTests
{
    internal static EntityRecognizer Recognizer { get; } = new(
    [
        new("Supplier", "Fabrikam Seals", ["Fabrikam"]),
        new("Customer", "Tailspin Water Utility", ["Tailspin"]),
        new("Site", "Riverside plant", ["Riverside"]),
        new("Person", "Nadia Rahman", ["Nadia"]),
    ]);

    [Fact]
    public void Identifiers_and_names_become_canonical_IDs()
    {
        var found = Recognizer.Find("Fit PN-4471-C on P-201 under ECN-2291; Fabrikam ships it to Tailspin.");

        Assert.Equal(["part:PN-4471-C", "equipment:P-201", "changenotice:ECN-2291", "supplier:fabrikam-seals", "customer:tailspin-water-utility"], found.Select(e => e.Id));
        Assert.Equal("Fabrikam Seals", found.Single(e => e.Type == "Supplier").Name);
    }

    [Fact]
    public async Task An_asset_record_table_states_its_relations()
    {
        var source = await SourceAsync("P201-ASSET");

        var triples = RuleExtractor.FromTables(source, Recognizer).Select(t => (t.From.Id, t.Relation, t.To.Id)).ToList();

        Assert.Contains(("equipment:P-201", "INSTANCE_OF", "model:CP-200"), triples);
        Assert.Contains(("equipment:P-201", "HAS_COMPONENT", "equipment:V-114"), triples);
        Assert.Contains(("equipment:P-201", "HAS_FITTED", "part:PN-4471-C"), triples);
        Assert.Contains(("equipment:P-201", "LOCATED_AT", "site:riverside-plant"), triples);
    }

    [Fact]
    public async Task A_change_notice_relates_itself_to_what_its_prose_mentions()
    {
        var source = await SourceAsync("ECN-2291");

        var triples = RuleExtractor.FromSubject(source, Recognizer).Select(t => (t.From.Id, t.Relation, t.To.Id)).ToHashSet();

        Assert.Contains(("changenotice:ECN-2291", "AFFECTS", "part:PN-4471-B"), triples);
        Assert.Contains(("changenotice:ECN-2291", "AFFECTS", "part:PN-4471-C"), triples);
        Assert.Contains(("changenotice:ECN-2291", "RAISED_BY", "person:nadia-rahman"), triples);
    }

    [Fact]
    public async Task The_models_names_and_directions_are_repaired_and_its_inventions_dropped()
    {
        var source = await SourceAsync("SR-2026-1107");
        var chunk = new Chunk(source.Id, 0, source.Text.Length, source.Text);
        var entities = Recognizer.Find(source.Text).Append(RuleExtractor.Subject(source, Recognizer)!).ToList();
        var model = new ModelRelationExtractor(new ScriptedChat("""
            {"relations": [
              {"from": "SR-2026-1107", "relation": "INVOLVES", "to": "PN-4471-C"},
              {"from": "errorcode:E-217", "relation": "REPORTED", "to": "incident:SR-2026-1107"},
              {"from": "incident:SR-2026-1107", "relation": "SUPPLIED_BY", "to": "part:PN-4471-B"},
              {"from": "incident:SR-2026-1107", "relation": "INVOLVES", "to": "part:PN-9999-Z"}
            ]}
            """));

        var triples = await model.ExtractAsync(source, chunk, entities, TestContext.Current.CancellationToken);

        Assert.Equal([("incident:SR-2026-1107", "INVOLVES", "part:PN-4471-C"), ("incident:SR-2026-1107", "REPORTED", "errorcode:E-217")],
            triples.Select(t => (t.From.Id, t.Relation, t.To.Id)));
        Assert.Equal((2, 1, 1, 2), (model.Proposed, model.Renamed, model.Flipped, model.Rejected));
    }

    internal static async Task<ChunkSource> SourceAsync(string id)
    {
        var entry = GeneratedCorpus.Register().First(e => e.Id == id && e.CopyOf is null);
        var truth = await GeneratedCorpus.TruthAsync(entry);
        return new ChunkSource(id, truth.Metadata.Title, truth);
    }

    private sealed class ScriptedChat(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

public sealed class ProvenanceTests
{
    private static Mention M(string doc, string version = "1") => new(doc, version, 0, 10, "table", 0.9);

    private static KnowledgeGraph Graph()
    {
        var g = new KnowledgeGraph();
        foreach (var (id, type, doc) in new[] { ("part:PN-4471-C", "Part", "CAT"), ("supplier:fabrikam-seals", "Supplier", "CAT"), ("incident:SR-1", "Incident", "SR-1") })
        {
            g.AddNode(id, type, id, M(doc));
        }

        g.AddEdge("part:PN-4471-C", "SUPPLIED_BY", "supplier:fabrikam-seals", M("CAT"));
        g.AddEdge("part:PN-4471-C", "SUPPLIED_BY", "supplier:fabrikam-seals", M("MAIL"));
        g.AddEdge("incident:SR-1", "INVOLVES", "part:PN-4471-C", M("SR-1"));
        return g;
    }

    [Fact]
    public void Removing_a_version_removes_what_only_it_supported()
    {
        var g = Graph();

        var removed = g.RemoveVersion("SR-1", "1");

        Assert.Equal((2, 1, 1), removed);
        Assert.Null(g.Find("incident:SR-1"));
        Assert.Single(g.Edges);
    }

    [Fact]
    public void An_edge_stated_twice_survives_losing_one_source_and_its_confidence_shows_it()
    {
        var g = Graph();
        var edge = g.Edges.Single(e => e.Key.Relation == "SUPPLIED_BY");
        Assert.Equal(0.99, edge.Confidence, 3);

        g.RemoveVersion("MAIL", "1");

        Assert.Equal(0.9, g.Edges.Single(e => e.Key.Relation == "SUPPLIED_BY").Confidence, 3);
    }

    [Fact]
    public void A_reader_sees_only_what_their_documents_state()
    {
        var view = Graph().View(m => m.DocumentId != "SR-1");

        Assert.DoesNotContain(view.Edges, e => e.Key.Relation == "INVOLVES");
        Assert.Null(view.Find("incident:SR-1"));
    }

    [Fact]
    public async Task The_recursive_CTE_walks_like_the_graph_and_deletes_cascade()
    {
        var g = Graph();
        await using var duck = new DuckDBConnection("Data Source=:memory:");
        await duck.OpenAsync(TestContext.Current.CancellationToken);
        await GraphStore.CreateAsync(duck, g);

        var hops = await GraphStore.NeighborhoodAsync(duck, "incident:SR-1", 2);
        Assert.Equal([("incident:SR-1", 0), ("part:PN-4471-C", 1), ("supplier:fabrikam-seals", 2)], hops);

        await GraphStore.RemoveVersionAsync(duck, "SR-1", "1");
        Assert.Equal((4L, 1L, 2L), await GraphStore.CountAsync(duck));
    }
}

public sealed class ResolutionTests
{
    private static readonly (string, string)[] Known = [("Supplier", "Fabrikam Seals"), ("Supplier", "Litware Bearings"), ("Customer", "Tailspin Water Utility")];

    [Fact]
    public void A_legal_suffix_or_a_short_form_resolves_to_the_known_name()
    {
        var resolver = new EntityResolver();

        var map = resolver.Resolve(Known, [("Supplier", "Fabrikam Seals Ltd."), ("Customer", "Tailspin")]);

        Assert.Equal("Fabrikam Seals", map[("Supplier", "Fabrikam Seals Ltd.")]);
        Assert.Equal("Tailspin Water Utility", map[("Customer", "Tailspin")]);
        Assert.All(resolver.Log, d => Assert.True(d.Applied));
    }

    [Fact]
    public void Identifiers_are_never_fuzzy_matched_and_a_split_stays_split()
    {
        Assert.True(EntityResolver.JaroWinkler("PN-4471-B", "PN-4471-C") > 0.95);
        var resolver = new EntityResolver();
        resolver.KeepApart.Add(("Tailspin Water Utility", "Tailspin"));

        var map = resolver.Resolve(Known, [("Part", "pn-4471-b"), ("Customer", "Tailspin")]);

        Assert.Equal("PN-4471-B", map[("Part", "pn-4471-b")]);
        Assert.Equal("Tailspin", map[("Customer", "Tailspin")]);
        Assert.Equal("kept apart by a split", Assert.Single(resolver.Log).Reason);
    }

    [Fact]
    public void Jaro_Winkler_matches_the_textbook_value() => Assert.Equal(0.961, EntityResolver.JaroWinkler("martha", "marhta"), 3);
}

public sealed class SearchTests
{
    /// <summary>Two triangles joined by one edge: a, b, c and x, y, z, with c to x.</summary>
    private static KnowledgeGraph Triangles()
    {
        var g = new KnowledgeGraph();
        var m = new Mention("D", "1", 0, 1, "table", 1);
        foreach (var n in new[] { "a", "b", "c", "x", "y", "z" }) g.AddNode(n, "Part", n, m);
        foreach (var (f, t) in new[] { ("a", "b"), ("b", "c"), ("a", "c"), ("x", "y"), ("y", "z"), ("x", "z"), ("c", "x") }) g.AddEdge(f, "SUPERSEDED_BY", t, m);
        return g;
    }

    [Fact]
    public void Personalized_PageRank_stays_near_the_seed()
    {
        var rank = new GraphSearch(Triangles(), ExtractionTests.Recognizer).PersonalizedPageRank(["a"]);

        Assert.True(rank["a"] > rank["b"] && rank["b"] > rank["x"] && rank["x"] > rank["z"]);
    }

    [Fact]
    public void The_shortest_path_crosses_the_bridge()
    {
        var path = Assert.Single(new GraphSearch(Triangles(), ExtractionTests.Recognizer).Paths("a", "z"));

        Assert.Equal(3, path.Count);
        Assert.Contains(path, e => e.Key == new EdgeKey("c", "SUPERSEDED_BY", "x"));
    }

    [Fact]
    public void Louvain_finds_the_two_triangles()
    {
        var g = Triangles();

        var community = Louvain.Detect(g);

        Assert.Equal(2, community.Values.Distinct().Count());
        Assert.Equal(community["a"], community["c"]);
        Assert.NotEqual(community["c"], community["x"]);
        Assert.True(Louvain.Modularity(g, community) > 0.3);
    }
}
