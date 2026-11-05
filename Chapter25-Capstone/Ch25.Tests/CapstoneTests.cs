using System.Diagnostics;
using Ch25.Knowledge.Api;
using Ch25.Readiness.Cli;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Governance;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Ch25.Tests;

/// <summary>One deployment of the reference implementation shared by a class: its first start builds the index.</summary>
public sealed class DeploymentFixture : IAsyncLifetime
{
    public ReferenceDeployment Service { get; } = new(Directory.CreateTempSubdirectory("ch25-").FullName);

    public async ValueTask InitializeAsync() => await Service.Stack.LiveAsync();

    public async ValueTask DisposeAsync() => await Service.DisposeAsync();
}

public sealed class CapstoneTests(DeploymentFixture fixture) : IClassFixture<DeploymentFixture>
{
    private const string Torque = "What torque applies to the M12 flange bolts on P-201?";
    private const string ParentalLeave = "What changed in the parental leave policy this year?";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_needs_a_valid_token_from_Contosos_tenant()
    {
        var service = fixture.Service;
        Assert.Equal(401, (await service.AskAsync(Torque, (string?)null, Cancel)).Status);
        Assert.Equal(403, (await service.AskAsync(Torque, service.Token(People.Tomas, tenant: Guid.Empty.ToString()), Cancel)).Status);
        var (status, reply) = await service.AskAsync(Torque, People.Tomas, Cancel);
        Assert.Equal(200, status);
        Assert.Equal("answered", reply!.Status);
        Assert.All(reply.Claims, c => Assert.True(c.Verified));
    }

    [Fact]
    public void Only_questions_that_chain_relations_take_the_graph_route()
    {
        Assert.True(GraphRoute.IsMultiHop("Which suppliers provide parts affected by ECN-2291, and do any have open quality incidents?"));
        Assert.False(GraphRoute.IsMultiHop(Torque));
        Assert.DoesNotContain(GoldenSet.Load(ContosoIndex.GoldenSetPath), q => GraphRoute.IsMultiHop(q.Question));
    }

    [Fact]
    public async Task The_graph_route_stays_inside_the_askers_view()
    {
        var live = await fixture.Service.Stack.LiveAsync(Cancel);
        const string Question = "Which suppliers provide parts affected by ECN-2291, and do any have open quality incidents?";
        var grace = await live.Retriever.RetrieveAsync(new(Question, People.Grace, 4), Cancel);
        Assert.Contains(grace.Hits, h => h.Chunk.Document.Id == "PARTS-CAT-2027");
        foreach (var person in new[] { People.Tomas, People.Daniel, People.Kwame })
        {
            var hits = (await live.Retriever.RetrieveAsync(new(Question, person, 4), Cancel)).Hits;
            Assert.All(hits, h => Assert.True(person.CanRead(h.Chunk.Document)));
        }
    }

    [Fact]
    public async Task The_asker_comes_from_the_token()
    {
        var kwame = (await fixture.Service.AskAsync(ParentalLeave, People.Kwame, Cancel)).Reply!;
        var daniel = (await fixture.Service.AskAsync(ParentalLeave, People.Daniel, Cancel)).Reply!;
        Assert.Equal("not_found", kwame.Status);
        Assert.Empty(kwame.Sources);
        Assert.Contains(daniel.Sources, s => s.DocumentId == "HR-PL-2027");
    }

    [Fact]
    public async Task Every_answer_is_recorded_in_a_chain_that_survives_a_restart()
    {
        var folder = Directory.CreateTempSubdirectory("ch25-").FullName;
        await using (var first = new ReferenceDeployment(folder))
        {
            await first.AskAsync(Torque, People.Tomas, Cancel);
            await first.AskAsync(ParentalLeave, People.Daniel, Cancel);
        }

        await using var restarted = new ReferenceDeployment(folder);
        var reply = (await restarted.AskAsync(Torque, People.Nadia, Cancel)).Reply!;
        var parts = await restarted.Stack.ReadyAsync(Cancel);
        var records = parts.Provenance.Read().ToList();

        Assert.Equal(3, reply.Provenance);
        Assert.Equal([1L, 2L, 3L], records.Select(r => r.Sequence));
        Assert.Null(parts.Provenance.FirstBroken());
        Assert.All(records, r => Assert.Equal(parts.Configuration, r.Configuration));
        Assert.All(records, r => Assert.Equal("v1-shipped", r.IndexVersion));
    }

    [Fact]
    public async Task An_answer_is_cached_by_its_evidence_until_that_document_is_erased()
    {
        var service = fixture.Service;
        const string question = "Which mechanical seal should I fit on P-201 now?";
        var first = (await service.AskAsync(question, People.Tomas, Cancel)).Reply!;
        var second = (await service.AskAsync(question, People.Tomas, Cancel)).Reply!;
        var parts = await service.Stack.ReadyAsync(Cancel);
        await parts.Open.Cache.InvalidateDocumentAsync(first.Sources[0].DocumentId, Cancel);
        var third = (await service.AskAsync(question, People.Tomas, Cancel)).Reply!;

        Assert.False(first.Cached);
        Assert.True(second.Cached);
        Assert.False(third.Cached);
        Assert.Equal(first.Text, third.Text);
    }

    [Fact]
    public async Task Keyword_search_answers_alone_when_the_embedding_model_fails()
    {
        await using var service = new ReferenceDeployment(Directory.CreateTempSubdirectory("ch25-").FullName,
            services: s => s.AddSingleton(new DenseDecorator(dense => new FailingRetriever(dense))));
        var reply = (await service.AskAsync("What does error E-217 mean on a CP-200, and how do I clear it?", People.Kwame, Cancel)).Reply!;

        Assert.Equal("answered", reply.Status);
        Assert.Contains(reply.Degraded, d => d.StartsWith("dense", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_models_spans_belong_to_the_requests_trace()
    {
        var spans = new List<Activity>();
        await using var service = new ReferenceDeployment(Directory.CreateTempSubdirectory("ch25-").FullName,
            services: s => s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(spans)));
        await service.Stack.LiveAsync(Cancel);
        await service.AskAsync("Which supplier provides the PN-4471-C seals, and when does the contract end?", People.Grace, Cancel);

        var ask = Assert.Single(spans, s => s.DisplayName == "ask");
        var model = spans.Where(s => s.DisplayName is "answer" or "validate" || s.GetTagItem("gen_ai.operation.name") is not null).ToList();
        Assert.Equal(3, model.Count);
        Assert.All(model, s => Assert.Equal(ask.TraceId, s.TraceId));
    }

    [Fact]
    public async Task A_check_that_throws_fails_and_any_failure_blocks_the_release()
    {
        var parts = await fixture.Service.Stack.ReadyAsync(Cancel);
        var context = new ReviewContext(fixture.Service, parts, parts.Open.Model, Directory.CreateTempSubdirectory("ch25-").FullName);
        var results = await Review.RunAsync(context,
        [
            new Check("T1", "test", "passes", "25", "nobody", _ => Task.FromResult(new Finding(CheckStatus.Pass, "fine"))),
            new Check("T2", "test", "throws", "25", "nobody", _ => throw new InvalidOperationException("broken probe")),
            new Check("T3", "test", "warns", "25", "nobody", _ => Task.FromResult(new Finding(CheckStatus.Warn, "a condition"))),
        ]);

        Assert.Equal([CheckStatus.Pass, CheckStatus.Fail, CheckStatus.Warn], results.Select(r => r.Finding.Status));
        Assert.Contains("broken probe", results[1].Finding.Evidence, StringComparison.Ordinal);
        Assert.StartsWith("not ready", Review.Decision(results), StringComparison.Ordinal);
        Assert.StartsWith("ready with conditions", Review.Decision([results[0], results[2]]), StringComparison.Ordinal);
    }
}
