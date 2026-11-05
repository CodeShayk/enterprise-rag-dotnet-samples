using System.Numerics.Tensors;
using Ch02.Fundamentals;
using Contoso.Corpus;

namespace Ch02.Tests;

public sealed class FundamentalsTests
{
    private const string Paraphrase = "How tight should the screws on the P-201 pump flanges be?";

    [Fact]
    public void Simd_and_scalar_cosine_agree()
    {
        var random = new Random(7);
        float[] a = [.. Enumerable.Range(0, 768).Select(_ => (float)random.NextDouble() - 0.5f)];
        float[] b = [.. Enumerable.Range(0, 768).Select(_ => (float)random.NextDouble() - 0.5f)];

        Assert.InRange(Math.Abs(Similarity.ScalarCosine(a, b) - Similarity.Cosine(a, b)), 0, 1e-5);
    }

    [Fact]
    public void For_unit_vectors_the_dot_product_is_the_cosine()
    {
        var random = new Random(7);
        var a = Similarity.RandomUnitVector(random, 384);
        var b = Similarity.RandomUnitVector(random, 384);

        Assert.InRange(Math.Abs(TensorPrimitives.Dot(a, b) - Similarity.Cosine(a, b)), 0, 1e-5);
    }

    [Fact]
    public void Exact_terms_rank_the_torque_table_first()
    {
        var index = InvertedIndex.Create(SeedDocuments.All);

        Assert.Equal("CP200-MAN-2009-6.3", index.Search("torque for M12 flange bolts", People.Tomas)[0].Document.Id);
    }

    [Fact]
    public void Without_stemming_the_paraphrase_matches_nothing_but_pump()
    {
        var hits = InvertedIndex.Create(SeedDocuments.All).Search(Paraphrase, People.Tomas);

        Assert.All(hits, hit => Assert.Equal(["pump"], hit.MatchedTerms));
    }

    [Fact]
    public void Stemming_lets_the_paraphrase_find_the_torque_table()
    {
        var top = InvertedIndex.Create(SeedDocuments.All, stem: true).Search(Paraphrase, People.Tomas)[0];

        Assert.Equal("CP200-MAN-2009-6.3", top.Document.Id);
        Assert.Contains("flange", top.MatchedTerms);
    }

    [Fact]
    public void Rare_terms_outweigh_common_ones()
    {
        var index = InvertedIndex.Create(SeedDocuments.All, stem: true);

        Assert.True(index.Idf("m12") > index.Idf("seal"));
    }

    [Fact]
    public void An_identifier_costs_several_tokens()
    {
        Assert.True(new TokenCounter("o200k_base").Count("PN-4471-B") > 1);
    }
}
