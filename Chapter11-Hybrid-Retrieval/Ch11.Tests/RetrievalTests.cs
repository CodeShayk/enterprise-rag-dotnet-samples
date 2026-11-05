using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Retrieval;

namespace Ch11.Tests;

public sealed class FusionTests
{
    [Fact]
    public void Rrf_adds_reciprocal_ranks_and_ignores_scores()
    {
        var keyword = Hits(("x", 40.0), ("y", 12.0), ("z", 3.0));
        var dense = Hits(("y", 0.81), ("w", 0.80));

        var fused = new ReciprocalRankFusion(60).Fuse([keyword, dense], 4);

        Assert.Equal(["y", "x", "w", "z"], fused.Select(h => h.Chunk.Chunk.Doc));
        Assert.Equal(1.0 / 62 + 1.0 / 61, fused[0].Score, 12);
        Assert.Equal(1.0 / 62, fused[2].Score, 12);
    }

    [Fact]
    public void Convex_fusion_normalizes_each_ranking_first()
    {
        // Raw BM25 scores would swamp cosines; after min-max both run from 0 to 1.
        var keyword = Hits(("a", 30.0), ("b", 10.0));
        var dense = Hits(("b", 0.90), ("a", 0.50));

        var even = new ConvexFusion(0.5).Fuse([keyword, dense], 2);
        var keywordHeavy = new ConvexFusion(0.8).Fuse([keyword, dense], 2);

        Assert.Equal(0.5, even[0].Score, 12);
        Assert.Equal("a", keywordHeavy[0].Chunk.Chunk.Doc);
        Assert.Equal(0.8, keywordHeavy[0].Score, 12);
    }

    internal static IReadOnlyList<Hit> Hits(params (string Doc, double Score)[] hits) =>
        [.. hits.Select((h, i) => new Hit(Fixtures.Chunk(i, h.Doc), h.Score, "test"))];
}

public sealed class KeywordTests
{
    [Fact]
    public void Contoso_analyzer_keeps_identifiers_whole_in_parts_and_run_together()
    {
        var terms = ContosoAnalyzers.Terms(ContosoAnalyzers.Identifiers(), "Fit PN-4471-C after error E-217.");

        Assert.Contains("pn-4471-c", terms);
        Assert.Contains("4471", terms);
        Assert.Contains("pn4471c", terms);
        Assert.Contains("e-217", terms);
        Assert.Contains("e217", terms);
    }

    [Fact]
    public void Glossary_makes_an_acronym_and_its_words_interchangeable()
    {
        var glossary = ContosoAnalyzers.Glossary(Path.Combine(AppContext.BaseDirectory, "contoso-glossary.txt"));
        var terms = ContosoAnalyzers.Terms(ContosoAnalyzers.Identifiers(glossary), "LOTO before work");

        Assert.Contains("lockout", terms);
        Assert.Contains("tagout", terms);
    }

    [Fact]
    public void Bm25_saturates_repeated_terms_and_b_sets_the_length_penalty()
    {
        IReadOnlyList<string> Repeat(int times, int length) => [.. Enumerable.Repeat("seal", times).Concat(Enumerable.Repeat("filler", length - times))];
        var index = new Bm25Index([Repeat(1, 20), Repeat(10, 20), Repeat(1, 40), ["other"]], k1: 1.2, b: 0.75);
        var flat = new Bm25Index([Repeat(1, 20), Repeat(10, 20), Repeat(1, 40), ["other"]], k1: 1.2, b: 0);

        var scores = index.Score(["seal"]);
        var unnormalized = flat.Score(["seal"]);

        Assert.True(scores[1] / scores[0] < 2.2);                       // ten times the term, not ten times the score
        Assert.True(scores[2] < scores[0]);                              // the same term in a longer chunk weighs less
        Assert.Equal(unnormalized[0], unnormalized[2], 12);              // unless b is 0
    }

    [Fact]
    public async Task Lucene_applies_permissions_and_metadata_as_filters()
    {
        var chunks = new[]
        {
            Fixtures.Chunk(0, "MAN", "seal water pressure low", collection: "manuals", groups: ["fluid-engineering"]),
            Fixtures.Chunk(1, "CONTRACT", "seal water pressure warranty", collection: "policies and bulletins", groups: ["procurement"]),
            Fixtures.Chunk(2, "SOP-REV2", "seal water pressure check", collection: "procedures", groups: ["fluid-engineering"],
                from: new DateOnly(2024, 3, 4), to: new DateOnly(2026, 9, 13)),
            Fixtures.Chunk(3, "SOP-REV3", "seal water pressure check", collection: "procedures", groups: ["fluid-engineering"],
                from: new DateOnly(2026, 9, 14)),
        };
        using var lucene = new LuceneRetriever("test", chunks, ContosoAnalyzers.Identifiers());
        var engineer = new Person("e", "Engineer", "engineer", "fluid", new HashSet<string> { "fluid-engineering" });

        var all = await lucene.RetrieveAsync(new RetrievalRequest("seal water pressure", engineer, 10), TestContext.Current.CancellationToken);
        var procedures = await lucene.RetrieveAsync(new RetrievalRequest("seal water pressure", engineer, 10, new RetrievalFilter { Collections = ["procedures"] }), TestContext.Current.CancellationToken);
        var asOf = await lucene.RetrieveAsync(new RetrievalRequest("seal water pressure", engineer, 10, new RetrievalFilter { AsOf = new DateOnly(2027, 1, 1) }), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(all.Hits, h => h.Chunk.Chunk.Doc == "CONTRACT");
        Assert.Equal(["SOP-REV2", "SOP-REV3"], procedures.Hits.Select(h => h.Chunk.Chunk.Doc).Order());
        Assert.DoesNotContain(asOf.Hits, h => h.Chunk.Chunk.Doc == "SOP-REV2");
    }
}

public sealed class HybridTests
{
    private static readonly Person Asker = new("t", "Tester", "engineer", "fluid", new HashSet<string> { "everyone" });

    [Fact]
    public async Task A_late_source_is_reported_and_the_search_goes_on_without_it()
    {
        var fast = new Fake("keyword", FusionTests.Hits(("a", 2.0), ("b", 1.0)));
        var late = new Fake("dense", FusionTests.Hits(("c", 0.9)), delay: TimeSpan.FromSeconds(15));
        // A limit a busy machine can't push the fast source past.
        var hybrid = new HybridRetriever("hybrid", [fast, late], new ReciprocalRankFusion(60), timeout: TimeSpan.FromSeconds(3));

        var stopwatch = Stopwatch.StartNew();
        var result = await hybrid.RetrieveAsync(new RetrievalRequest("q", Asker, 4), TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12));
        Assert.Equal(["a", "b"], result.Hits.Select(h => h.Chunk.Chunk.Doc));
        Assert.Equal(["dense: no answer within 3000 ms"], result.Degraded);
    }

    [Fact]
    public async Task A_failing_source_is_reported_and_the_rest_are_fused()
    {
        var hybrid = new HybridRetriever("hybrid",
            [new Fake("keyword", FusionTests.Hits(("a", 2.0))), new Fake("dense", [], fail: true), new Fake("sparse", FusionTests.Hits(("b", 5.0)))],
            new ReciprocalRankFusion(60));

        var result = await hybrid.RetrieveAsync(new RetrievalRequest("q", Asker, 4), TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], result.Hits.Select(h => h.Chunk.Chunk.Doc));
        Assert.Equal(["dense: InvalidOperationException"], result.Degraded);
    }

    private sealed class Fake(string name, IReadOnlyList<Hit> hits, TimeSpan delay = default, bool fail = false) : IRetriever
    {
        public string Name => name;

        public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
        {
            if (fail) throw new InvalidOperationException("down");
            Thread.Sleep(delay);   // ignores the token, like a stuck dependency
            return Task.FromResult(RetrievalResult.Of(hits));
        }
    }
}

internal static class Fixtures
{
    public static IndexedChunk Chunk(int ordinal, string doc, string text = "text", string collection = "manuals", string[]? groups = null,
        DateOnly? from = null, DateOnly? to = null)
    {
        var document = new SourceDocument(doc, doc, "fluid", groups ?? ["everyone"], text);
        return new IndexedChunk(ordinal, new Chunk(doc, 0, text.Length, text), document, collection, "manual", [], from, to);
    }
}
