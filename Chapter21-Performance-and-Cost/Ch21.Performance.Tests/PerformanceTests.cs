using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Performance;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Ch21.Performance.Tests;

internal static class Fixture
{
    public static EvidenceBlock Block(string id, string document, string text, int start = 0) =>
        new(id, document, document, null, "manual", null, [new EvidenceSegment(text, start, start + text.Length)], 0.5);

    public static Answer Answer(AnswerStatus status, params Claim[] claims) =>
        new(status, "text", claims, [Block("S1", "CP200-MAN-2009", "Tighten the M12 flange bolts to 85 N·m.")],
            claims.Length == 0 ? 0 : claims.Count(c => c.Valid) / (double)claims.Length, "grounded-test", new AnswerTimings(0, 0, 0, 0, 0), [], []);

    public static Claim Valid { get; } = new("The bolts take 85 N·m.", "S1", "85 N·m", SourceExists: true, QuoteFound: true);

    public static Claim Invalid { get; } = new("The bolts take 95 N·m.", "S1", "95 N·m", SourceExists: true, QuoteFound: false);
}

public sealed class CostTests
{
    [Fact]
    public void Local_cost_is_every_cpu_second_at_the_core_hour_price()
    {
        var usage = new AnswerUsage(AppCpuSeconds: 2.78, ModelCpuSeconds: 162.8, InputTokens: 1_042, OutputTokens: 126, WallSeconds: 17.2);
        Assert.Equal(165.58m / 3600m * 0.05m, CostModel.Local(usage, new Prices()));
    }

    [Fact]
    public void Api_cost_is_the_tokens_plus_the_applications_own_cpu()
    {
        var usage = new AnswerUsage(2.78, 162.8, 1_042, 126, 17.2);
        Assert.Equal((1_042m * 1m + 126m * 4m) / 1_000_000m + 2.78m / 3600m * 0.05m, CostModel.Api(usage, new Prices()));
    }

    [Fact]
    public void A_node_costs_its_cores_per_hour_divided_by_the_answers_it_serves()
    {
        Assert.Equal(20 * 0.05m / 250m, CostModel.PerAnswerOnNode(20, 250, new Prices()));
        Assert.Equal(0m, CostModel.PerAnswerOnNode(20, 0, new Prices()));
    }
}

public sealed class CacheKeyTests
{
    [Fact]
    public void An_answer_key_follows_the_evidence_not_the_person()
    {
        var contract = Fixture.Block("S1", "SC-2024-118", "The contract runs from July 1, 2024 to June 30, 2027.");
        var catalog = Fixture.Block("S1", "PARTS-CAT-2027", "PN-4471-C | Fabrikam Seals");
        const string question = "Which supplier provides the PN-4471-C seals, and when does the contract end?";
        Assert.NotEqual(CacheKeys.Answer("p", "m", question, [contract, catalog]), CacheKeys.Answer("p", "m", question, [catalog]));
        Assert.Equal(CacheKeys.Answer("p", "m", question, [catalog]), CacheKeys.Answer("p", "m", question.ToUpperInvariant() + "  ", [catalog]));
        Assert.NotEqual(CacheKeys.Answer("p", "m", question, [catalog]), CacheKeys.Answer("p2", "m", question, [catalog]));
    }

    [Fact]
    public void An_answer_key_changes_when_a_document_is_reindexed_with_new_text()
    {
        var v2 = Fixture.Block("S1", "SOP-FS-014", "Torque the gland nuts to 22 N·m.");
        var v3 = Fixture.Block("S1", "SOP-FS-014", "Torque the gland nuts to 25 N·m.");
        Assert.NotEqual(CacheKeys.Answer("p", "m", "q", [v2]), CacheKeys.Answer("p", "m", "q", [v3]));
    }

    [Fact]
    public void A_retrieval_key_holds_the_askers_access_and_the_index_version()
    {
        Assert.Equal(CacheKeys.Retrieval(People.Tomas, "v1", "q"), CacheKeys.Retrieval(People.Nadia, "v1", "q"));
        Assert.NotEqual(CacheKeys.Retrieval(People.Tomas, "v1", "q"), CacheKeys.Retrieval(People.Grace, "v1", "q"));
        Assert.NotEqual(CacheKeys.Retrieval(People.Tomas, "v1", "q"), CacheKeys.Retrieval(People.Tomas, "v2", "q"));
    }

    [Fact]
    public async Task Invalidating_a_document_removes_only_the_answers_that_used_it()
    {
        var cache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();
        var answers = new AnswerCache(cache, "p", "m");
        var sop = Fixture.Block("S1", "SOP-FS-014", "Torque the gland nuts to 22 N·m.");
        var manual = Fixture.Block("S1", "CP200-MAN-2009", "Tighten the M12 flange bolts to 85 N·m.");
        var computed = 0;
        ValueTask<Answer> Compute(CancellationToken _)
        {
            computed++;
            return ValueTask.FromResult(Fixture.Answer(AnswerStatus.Answered, Fixture.Valid));
        }

        await answers.GetOrAnswerAsync("gland nuts", [sop], Compute, TestContext.Current.CancellationToken);
        await answers.GetOrAnswerAsync("flange bolts", [manual], Compute, TestContext.Current.CancellationToken);
        Assert.True((await answers.GetOrAnswerAsync("gland nuts", [sop], Compute, TestContext.Current.CancellationToken)).Hit);
        await answers.InvalidateDocumentAsync("SOP-FS-014", TestContext.Current.CancellationToken);
        Assert.False((await answers.GetOrAnswerAsync("gland nuts", [sop], Compute, TestContext.Current.CancellationToken)).Hit);
        Assert.True((await answers.GetOrAnswerAsync("flange bolts", [manual], Compute, TestContext.Current.CancellationToken)).Hit);
        Assert.Equal(3, computed);
    }
}

public sealed class SemanticCacheTests
{
    private static float[] Vector(params float[] values)
    {
        var norm = MathF.Sqrt(values.Sum(v => v * v));
        return [.. values.Select(v => v / norm)];
    }

    [Fact]
    public void The_guard_refuses_a_close_question_about_another_identifier()
    {
        var cache = new SemanticCache<string>(threshold: 0.95);
        cache.Add("fluid", "What torque applies to the M12 flange bolts on P-201?", Vector(1, 0.1f), "85 N·m");
        var near = cache.Find("fluid", "What torque applies to the M16 flange bolts on P-201?", Vector(1, 0.11f))!.Value;
        Assert.True(near.Similarity > 0.99);
        Assert.False(near.Hit);
        Assert.True(cache.Find("fluid", "How much torque do the M12 flange bolts on P-201 need?", Vector(1, 0.11f))!.Value.Hit);
    }

    [Fact]
    public void Another_scope_never_sees_the_entry()
    {
        var cache = new SemanticCache<string>(threshold: 0.5);
        cache.Add("procurement", "When does the Fabrikam Seals contract end?", Vector(1, 0), "June 30, 2027");
        Assert.Null(cache.Find("fluid", "When does the Fabrikam Seals contract end?", Vector(1, 0)));
    }
}

public sealed class CascadeTests
{
    [Fact]
    public void Only_an_answer_with_every_claim_verified_stands()
    {
        Assert.True(ModelCascade.Accept(Fixture.Answer(AnswerStatus.Answered, Fixture.Valid)));
        Assert.True(ModelCascade.Accept(Fixture.Answer(AnswerStatus.Partial, Fixture.Valid)));
        Assert.False(ModelCascade.Accept(Fixture.Answer(AnswerStatus.Answered, Fixture.Valid, Fixture.Invalid)));
        Assert.False(ModelCascade.Accept(Fixture.Answer(AnswerStatus.NotFound)));
    }
}

public sealed class LimitTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_quota_refuses_what_would_overspend_it_and_resets_the_next_day()
    {
        var clock = new Clock(new DateTimeOffset(2027, 2, 1, 9, 0, 0, TimeSpan.Zero));
        var budget = new TenantTokenBudget(new Dictionary<string, long> { ["fluid"] = 3_000 }, clock);
        Assert.True(budget.TryReserve("fluid", 1_300));
        budget.Settle("fluid", 1_300, 1_000);
        Assert.True(budget.TryReserve("fluid", 1_300));
        Assert.False(budget.TryReserve("fluid", 1_300));
        Assert.False(budget.TryReserve("service", 1));
        clock.Now = clock.Now.AddDays(1);
        Assert.True(budget.TryReserve("fluid", 1_300));
    }

    [Fact]
    public async Task The_gate_sheds_what_its_queue_cant_hold()
    {
        using var gate = new ModelGate(concurrent: 1, queue: 2);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var lease = await gate.EnterAsync(TestContext.Current.CancellationToken);
            if (!lease.IsAcquired) return false;
            await Task.Delay(50, TestContext.Current.CancellationToken);
            return true;
        }));
        Assert.Equal(3, outcomes.Count(o => o));
    }

    [Fact]
    public async Task A_closed_loop_keeps_each_worker_busy_until_the_time_is_up()
    {
        var result = await ClosedLoopLoad.RunAsync(2, TimeSpan.FromMilliseconds(400), (_, ct) => Task.Delay(50, ct), TestContext.Current.CancellationToken);
        // Two workers repeat a 50 ms call for 400 ms. A busy machine stretches the calls, so only the ceiling is exact.
        Assert.InRange(result.Completed, 4, 18);
        Assert.True(result.Percentile(0.5) >= 0.04);
        Assert.True(result.PerHour > 0);
    }
}
