using Contoso.Knowledge.Operations;

namespace Ch24.Deployment.Tests;

public sealed class IndexCatalogTests
{
    private static readonly DateTimeOffset Now = new(2027, 2, 1, 7, 0, 0, TimeSpan.Zero);

    private static IndexCatalog Catalog(params (string Version, string Model)[] versions)
    {
        var catalog = new IndexCatalog(Directory.CreateTempSubdirectory().FullName);
        foreach (var (version, model) in versions) catalog.SaveManifest(new IndexManifest(version, "parent-child-100-600", true, model, "bf64d05457cb", 219, "corpus", Now));
        return catalog;
    }

    [Fact]
    public void A_swap_keeps_the_previous_version_and_a_rollback_returns_to_it()
    {
        var catalog = Catalog(("v1", "bge-small-en-v1.5-int8"), ("v2", "bge-small-en-v1.5-int8"));
        catalog.Swap("live", "v1", "bge-small-en-v1.5-int8", Now);
        var swapped = catalog.Swap("live", "v2", "bge-small-en-v1.5-int8", Now);
        Assert.Equal(("v2", "v1"), (swapped.Version, swapped.Previous));

        var back = catalog.Rollback("live", "bge-small-en-v1.5-int8", Now);
        Assert.Equal(("v1", "v2"), (back.Version, back.Previous));
        Assert.Empty(Directory.GetFiles(Path.Combine(catalog.Root, "aliases"), "*.tmp"));
    }

    [Fact]
    public void A_swap_refuses_an_index_the_service_cannot_query()
    {
        var catalog = Catalog(("v1", "bge-small-en-v1.5-int8"), ("v2", "bge-small-en-v1.5-fp32"));
        catalog.Swap("live", "v1", "bge-small-en-v1.5-int8", Now);
        Assert.Throws<InvalidOperationException>(() => catalog.Swap("live", "v2", "bge-small-en-v1.5-int8", Now));
        Assert.Equal("v1", catalog.Alias("live")!.Version);
        Assert.Throws<InvalidOperationException>(() => Catalog(("v1", "bge-small-en-v1.5-int8")).Rollback("live", "bge-small-en-v1.5-int8", Now));
    }
}

public sealed class ReleaseGateTests
{
    private static readonly GatePolicy Policy = new(0.03, new HashSet<string>(["G001", "G017"]));

    private static List<(string, double, double)> Scores(int same, params (string Id, double Live, double Candidate)[] changes) =>
        [.. Enumerable.Range(0, same).Select(i => ($"Q{i}", 1.0, 1.0)), .. changes];

    [Fact]
    public void A_lost_must_pass_question_fails_the_gate_even_when_the_average_improves()
    {
        var result = ReleaseGate.Evaluate(Scores(100, ("G017", 1, 0), ("Q200", 0, 1), ("Q201", 0, 1), ("Q202", 0, 1)), Policy);
        Assert.True(result.Change.Mean > 0);
        Assert.False(result.Passed);
        Assert.Equal(["G017"], result.MustPassLost);
        Assert.Equal(3, result.Improved.Count);
    }

    [Fact]
    public void Identical_runs_pass_and_a_loss_beyond_the_allowed_drop_fails()
    {
        Assert.True(ReleaseGate.Evaluate(Scores(120), Policy).Passed);
        var worse = ReleaseGate.Evaluate(Scores(110, ("Q300", 1, 0), ("Q301", 1, 0), ("Q302", 1, 0), ("Q303", 1, 0), ("Q304", 1, 0), ("Q305", 1, 0)), Policy);
        Assert.False(worse.Passed);
        Assert.Equal(6, worse.Regressed.Count);
    }
}

public sealed class PipelineTests
{
    [Fact]
    public void The_pipeline_runs_every_test_project_the_supply_check_and_the_release()
    {
        var pipeline = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pipeline", "contoso-knowledge.yml"));
        Assert.Contains("dotnet test --solution Contoso-Knowledge.slnx", pipeline, StringComparison.Ordinal);
        Assert.Contains("Ch22.Security.Cli -- supply", pipeline, StringComparison.Ordinal);
        Assert.Contains("Ch24.Deployment.Cli -- release", pipeline, StringComparison.Ordinal);
        Assert.Contains("environment: production", pipeline, StringComparison.Ordinal);
    }
}
