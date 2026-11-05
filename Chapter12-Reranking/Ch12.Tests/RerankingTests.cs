using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Retrieval;

namespace Ch12.Tests;

public sealed class CrossEncoderTests : IDisposable
{
    private readonly OnnxCrossEncoder _model = new("MiniLM-L6",
        Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "model.onnx"), Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt"));

    [Fact]
    public void The_passage_that_answers_scores_highest()
    {
        var scores = _model.Score("What does error E-217 mean on a CP-200?",
        [
            "Replace the cooling fan of the VD-75 drive every 12 weeks.",
            "Error E-217 means seal-water pressure low. The pump trips until the pressure is restored.",
            "Table 6-2 gives the tightening torque for the CP-200 flange bolts.",
        ]);

        Assert.Equal(1, Array.IndexOf(scores, scores.Max()));
    }

    [Fact]
    public void A_long_passage_is_cut_to_fit_and_scores_the_same_in_any_batch()
    {
        var question = "How do I clear E-217?";
        var longPassage = string.Join(" ", Enumerable.Repeat("The seal-water pressure must be at least 2.0 bar.", 200));
        var alone = _model.Score(question, [longPassage]);
        var batched = _model.Score(question, ["Short text.", longPassage, "Another short text."]);

        Assert.True(float.IsFinite(alone[0]));
        Assert.Equal(alone[0], batched[1], 3);
    }

    public void Dispose() => _model.Dispose();
}

public sealed class DiversityTests
{
    [Fact]
    public async Task Mmr_trades_a_near_duplicate_for_something_different()
    {
        // Two near-identical candidates on top, a different one third.
        var hits = Fixtures.Hits(("A", 5.0), ("B", 4.9), ("C", 3.0));
        var vectors = new Dictionary<string, float[]> { ["A"] = [1, 0], ["B"] = [0.99f, 0.14f], ["C"] = [0, 1] };
        var mmr = new MaximalMarginalRelevance(new Fixed(), c => vectors[c.Chunk.Doc], lambda: 0.5);

        var picked = await mmr.RerankAsync("q", hits, 2, TestContext.Current.CancellationToken);

        Assert.Equal(["A", "C"], picked.Select(h => h.Chunk.Chunk.Doc));
    }

    [Fact]
    public async Task A_per_document_cap_leaves_room_for_other_documents()
    {
        var hits = new[] { Fixtures.Hit(0, "MAN", 0, 5.0), Fixtures.Hit(1, "MAN", 100, 4.0), Fixtures.Hit(2, "MAN", 200, 3.0), Fixtures.Hit(3, "ASSET", 0, 1.0) };
        var capped = new MaximalMarginalRelevance(new Fixed(), _ => [1, 0], lambda: 1.0, perDocument: 2);

        var picked = await capped.RerankAsync("q", hits, 3, TestContext.Current.CancellationToken);

        Assert.Equal(["MAN", "MAN", "ASSET"], picked.Select(h => h.Chunk.Chunk.Doc));
    }

    /// <summary>Keeps the candidates' order and scores, as a re-ranker that has already decided.</summary>
    private sealed class Fixed : IReranker
    {
        public string Name => "fixed";

        public Task<IReadOnlyList<Hit>> RerankAsync(string question, IReadOnlyList<Hit> candidates, int top, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Hit>>([.. candidates.OrderByDescending(h => h.Score).Take(top)]);
    }
}

public sealed class CalibrationTests
{
    [Fact]
    public void Platt_scaling_recovers_a_known_curve()
    {
        // Logits from -6 to 6, labeled with the frequencies of P = sigmoid(2 * logit - 1).
        var samples = new List<(double, bool)>();
        for (var logit = -6.0; logit <= 6.0; logit += 0.5)
        {
            var positives = (int)Math.Round(100 * Calibration.Sigmoid(2 * logit - 1));
            for (var i = 0; i < 100; i++) samples.Add((logit, i < positives));
        }

        var fitted = Calibration.Fit(samples, iterations: 20000, rate: 0.1);

        Assert.InRange(fitted.A, 1.8, 2.2);
        Assert.InRange(fitted.B, -1.2, -0.8);
    }
}

internal static class Fixtures
{
    public static Hit Hit(int ordinal, string doc, int start, double score)
    {
        var text = new string('x', 400);
        var document = new SourceDocument(doc, doc, "fluid", ["everyone"], text);
        var chunk = new Chunk(doc, start, start + 50, text[start..(start + 50)]);
        return new Hit(new IndexedChunk(ordinal, chunk, document, "manuals", "manual", [], null, null), score, "test");
    }

    public static IReadOnlyList<Hit> Hits(params (string Doc, double Score)[] hits) =>
        [.. hits.Select((h, i) => Hit(i, h.Doc, 0, h.Score))];
}
