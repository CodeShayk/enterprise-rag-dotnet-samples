using System.Text.Json;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;

namespace Ch15.Tests;

public sealed class NliTests
{
    private sealed record Pair(string Premise, string Hypothesis, int[] Ids, float[] Logits);

    private sealed record Model(string[] Labels, Pair[] Pairs);

    public static TheoryData<string> Models => ["nli-deberta-v3-xsmall", "deberta-v3-xsmall-nli-binary"];

    [Theory]
    [MemberData(nameof(Models))]
    public void Tokens_and_logits_match_the_reference_pipeline(string name)
    {
        var folder = Path.Combine(EmbeddingModels.Root, name);
        Assert.SkipUnless(File.Exists(Path.Combine(folder, "model.onnx")), $"Run `python eng/get-models.py {name}` first.");
        var reference = JsonSerializer.Deserialize<Dictionary<string, Model>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference", "nli.json")), JsonSerializerOptions.Web)![name];
        using var model = new OnnxNliModel(name, folder, reference.Labels);

        var logits = model.Logits([.. reference.Pairs.Select(p => (p.Premise, p.Hypothesis))]);

        for (var i = 0; i < reference.Pairs.Length; i++)
        {
            Assert.Equal(reference.Pairs[i].Ids, model.Encode(reference.Pairs[i].Premise, reference.Pairs[i].Hypothesis));
            for (var j = 0; j < reference.Labels.Length; j++) Assert.Equal(reference.Pairs[i].Logits[j], logits[i][j], 0.01f);
        }
    }
}
