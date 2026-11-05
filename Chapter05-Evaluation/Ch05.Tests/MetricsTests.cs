using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;

namespace Ch05.Tests;

public sealed class MetricsTests
{
    // One ranking, worked by hand: the top 4 chunks have grades 0, 2, 1 and 0, and the corpus holds
    // three relevant chunks with grades 2, 1 and 1.
    private static readonly int[] Grades = [0, 2, 1, 0];
    private static readonly int[] Ideal = [2, 1, 1];

    [Fact]
    public void Every_metric_matches_the_hand_computation()
    {
        Assert.Equal(1.0, Metrics.HitAt(Grades, 4));
        Assert.Equal(0.5, Metrics.PrecisionAt(Grades, 4));                                  // 2 of 4
        Assert.Equal(0.5, Metrics.ReciprocalRank(Grades, 4));                               // first relevant at rank 2
        Assert.Equal((1 / 2.0 + 2 / 3.0) / 3, Metrics.AveragePrecisionAt(Grades, 4, 3), 6);  // (P@2 + P@3) / min(4, 3)
        Assert.Equal((2 / Math.Log2(3) + 1 / Math.Log2(4)) / (2 + 1 / Math.Log2(3) + 1 / Math.Log2(4)), Metrics.NdcgAt(Grades, Ideal, 4), 6);
        Assert.Equal((1 / 2.0 + 2 / 3.0) / 2, Metrics.ContextPrecision(Grades), 6);         // (P@2 + P@3) / 2 relevant
    }

    [Fact]
    public void A_chunk_is_relevant_when_it_holds_at_least_half_of_a_span()
    {
        LocatedSpan[] span = [new("DOC", 100, 150, 2, "a")];

        Assert.Equal(0, Metrics.Grade(new ChunkSpan("DOC", 0, 120), span));    // 20 of 50 characters
        Assert.Equal(2, Metrics.Grade(new ChunkSpan("DOC", 110, 300), span));  // 40 of 50 characters
        Assert.Equal(0, Metrics.Grade(new ChunkSpan("OTHER", 100, 150), span)); // same range, another document
    }

    [Fact]
    public void Recall_can_be_met_while_the_evidence_is_still_insufficient()
    {
        LocatedSpan[] span = [new("DOC", 100, 150, 2, "a")];
        ChunkSpan[] evidence = [new("DOC", 0, 140)]; // 40 of the 50 characters

        Assert.Equal(1.0, Metrics.RecallAt(evidence, span));
        Assert.Equal(0.0, Metrics.ContextRecall(evidence, span));
    }

    [Fact]
    public void Every_fact_must_be_present_for_full_context_recall()
    {
        LocatedSpan[] spans = [new("DOC", 0, 10, 2, "a"), new("DOC", 500, 520, 2, "b"), new("DOC", 600, 610, 1, "c")];
        ChunkSpan[] evidence = [new("DOC", 0, 100)];

        Assert.Equal(0.5, Metrics.ContextRecall(evidence, spans)); // fact a only; grade-1 spans aren't required
    }

    [Fact]
    public void Redundancy_counts_characters_the_evidence_repeats()
    {
        ChunkSpan[] evidence = [new("DOC", 0, 100), new("DOC", 80, 180), new("OTHER", 0, 50)];

        Assert.Equal(1 - 230 / 250.0, Metrics.Redundancy(evidence), 6); // 20 repeated characters of 250
    }

    [Fact]
    public void Kappa_matches_the_textbook_example()
    {
        // 20 both yes, 15 both no, 5 only the first, 10 only the second: observed 0.70, expected 0.50, kappa 0.40.
        var first = Enumerable.Repeat(true, 20).Concat(Enumerable.Repeat(false, 15)).Concat(Enumerable.Repeat(true, 5)).Concat(Enumerable.Repeat(false, 10)).ToList();
        var second = Enumerable.Repeat(true, 20).Concat(Enumerable.Repeat(false, 15)).Concat(Enumerable.Repeat(false, 5)).Concat(Enumerable.Repeat(true, 10)).ToList();

        Assert.Equal(0.4, Agreement.CohensKappa(first, second), 6);
    }

    [Fact]
    public void The_power_calculation_matches_the_McNemar_approximation()
    {
        // (1.96 x sqrt(0.2) + 0.84 x sqrt(0.2 - 0.01))^2 / 0.01 = 154.6, rounded up
        Assert.Equal(155, Power.PairedQuestions(discordant: 0.2, delta: 0.1));
    }

    [Fact]
    public void The_bootstrap_is_reproducible_and_centered_on_the_observed_difference()
    {
        double[] a = [0, 1, 1, 0, 1, 1, 0, 1];
        double[] b = [1, 1, 1, 0, 1, 1, 1, 1];

        var first = Bootstrap.Paired(a, b);
        var second = Bootstrap.Paired(a, b);

        Assert.Equal(first, second);
        Assert.Equal(0.25, first.Mean, 6);
        Assert.InRange(first.Mean, first.Low, first.High);
        Assert.Equal(new Difference(0, 0, 0), Bootstrap.Paired(a, a));
    }
}

public sealed class GoldenSetTests
{
    private static readonly IReadOnlyList<GoldenQuestion> Golden = GoldenSet.Load();
    private static readonly string[] Types = ["lookup", "procedural", "comparative", "numeric", "temporal", "multi-hop", "unanswerable"];

    [Fact]
    public void Every_span_is_found_in_a_document_its_asker_may_read()
    {
        var documents = MarkdownCorpus.Load().ToDictionary(d => d.Id);

        Assert.All(Golden, question => GoldenSet.Locate(question, documents));
    }

    [Fact]
    public void Ids_are_unique_and_every_question_has_a_known_type_and_split()
    {
        Assert.Equal(Golden.Count, Golden.Select(q => q.Id).Distinct().Count());
        Assert.All(Golden, q => Assert.Contains(q.Type, Types));
        Assert.All(Golden, q => Assert.Contains(q.Split, new[] { "dev", "test" }));
        Assert.All(Golden, q => Assert.Equal(q.Type == "unanswerable", !q.Answerable));
    }

    [Fact]
    public void Both_splits_cover_every_question_type()
    {
        foreach (var split in new[] { "dev", "test" })
        {
            Assert.All(Types, type => Assert.Contains(Golden, q => q.Split == split && q.Type == type));
        }
    }
}
