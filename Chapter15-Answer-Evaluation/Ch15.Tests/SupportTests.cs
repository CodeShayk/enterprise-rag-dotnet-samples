using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Ch15.Tests;

/// <summary>
/// Citation scoring and the NLI evaluator with the real 3-way xsmall model, on claims a person wouldn't dispute. The claims
/// are statements, not instructions: the model scores "Tighten the bolts to 85 N·m" far lower than "The bolts are tightened
/// to 85 N·m" against the same text.
/// </summary>
public sealed class SupportTests
{
    private const string Torque = "## 6.3 Flange bolt torque\nTighten the M12 flange bolts (property class 8.8, lightly oiled) to 85 N·m. Apply the torque in a cross pattern in three passes.";
    private const string Seal = "## 7.2 Service intervals\nReplace mechanical seal PN-4471-B every 8,000 operating hours.";

    private static OnnxNliModel? Model()
    {
        var folder = Path.Combine(EmbeddingModels.Root, "nli-deberta-v3-xsmall");
        return File.Exists(Path.Combine(folder, "model.onnx")) ? new OnnxNliModel("xsmall", folder, ["contradiction", "entailment", "neutral"]) : null;
    }

    [Fact]
    public void Citation_recall_counts_supported_statements_and_precision_counts_needed_citations()
    {
        using var model = Model();
        Assert.SkipWhen(model is null, "Run `python eng/get-models.py nli-deberta-v3-xsmall` first.");
        var scorer = new CitationScorer(new ClaimSupport(model!), 0.5);

        var score = scorer.Score(
        [
            new("The M12 flange bolts are tightened to 85 N·m.", [Torque]),             // supported by its citation
            new("The M12 flange bolts are tightened to 85 N·m.", [Seal, Torque]),       // supported; the seal citation isn't needed
            new("Mechanical seal PN-4471-B is replaced every 8,000 operating hours.", [Torque]),   // cites the wrong block
            new("Mechanical seal PN-4471-B is replaced every 8,000 operating hours.", []),         // cites nothing
        ]);

        Assert.Equal(new CitationScore(Statements: 4, Supported: 2, Citations: 4, Relevant: 2), score);
    }

    [Fact]
    public async Task The_NLI_evaluator_scores_the_share_of_sentences_the_evidence_entails()
    {
        using var model = Model();
        Assert.SkipWhen(model is null, "Run `python eng/get-models.py nli-deberta-v3-xsmall` first.");
        var evaluator = new NliGroundednessEvaluator(new ClaimSupport(model!), 0.5);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "The M12 flange bolts are tightened to 85 N·m. The bearings are replaced every 500 operating hours."));

        var result = await evaluator.EvaluateAsync([new(ChatRole.User, "What torque do the M12 flange bolts take?")], response, additionalContext: [new EvidenceContext(Torque)], cancellationToken: TestContext.Current.CancellationToken);
        var missing = await evaluator.EvaluateAsync([], response, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0.5, result.Get<NumericMetric>(NliGroundednessEvaluator.MetricName).Value);
        Assert.Contains(missing.Get<NumericMetric>(NliGroundednessEvaluator.MetricName).Diagnostics!, d => d.Severity == EvaluationDiagnosticSeverity.Error);
    }
}
