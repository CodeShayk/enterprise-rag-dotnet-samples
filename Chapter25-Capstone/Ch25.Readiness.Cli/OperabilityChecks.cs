using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ch25.Knowledge.Api;
using Contoso.Corpus;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static System.FormattableString;

namespace Ch25.Readiness.Cli;

/// <summary>Can the team run it: readiness, rollback, degradation, latency against the SLO and telemetry; and what wasn't run.</summary>
public static class OperabilityChecks
{
    public static IReadOnlyList<Check> All { get; } =
    [
        new("O1", "operability", "Healthy only with an index whose encoder matches", "24", "Knowledge Assistant team", HealthAsync),
        new("O2", "operability", "Rollback is one swap", "24", "Knowledge Assistant team", RollbackAsync),
        new("O3", "operability", "Keyword-only when the embedding model fails", "11, 24", "Knowledge Assistant team", DegradedAsync),
        new("O4", "operability", "Sources without an answer when the model fails", "16, 24", "Knowledge Assistant team", ModelDownAsync),
        new("O5", "operability", "Answer latency against the 15-second SLO", "20, 21", "Knowledge Assistant team", LatencyAsync),
        new("O6", "operability", "A trace for every answer, without content", "20", "Knowledge Assistant team", TelemetryAsync),
        new("N1", "operability", "Hybrid profile with a cloud model", "25", "Platform team", _ => NotRun("no cloud subscription here; published as a manifest only")),
        new("N2", "operability", "Sign-in through a Microsoft Entra ID tenant", "22", "Identity team", _ => NotRun("Entra-shaped tokens signed with a key made for the run")),
        new("N3", "operability", "The pipeline on a hosted runner", "24", "Platform team", _ => NotRun("Chapter 24's workflow ran locally only")),
        new("N4", "operability", "Load across replicas with a shared cache", "21", "Platform team", _ => NotRun("Chapter 21's load test ran on one instance")),
    ];

    private static Task<Finding> NotRun(string reason) => Task.FromResult(new Finding(CheckStatus.NotRun, reason));

    private static async Task<Finding> HealthAsync(ReviewContext c)
    {
        using var client = c.Service.CreateClient();
        var health = await client.GetStringAsync("/health");
        var alias = c.Service.Stack.Catalog.Alias(AssistantStack.AliasName)!;
        var manifest = c.Service.Stack.Catalog.Manifest(alias.Version);
        var ok = health == "Healthy" && manifest.EmbeddingModel == AssistantStack.QueryEncoder;
        return new(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"/health {health}; live -> {alias.Version}, {manifest.Chunks} chunks, {manifest.EmbeddingModel} ({manifest.EmbeddingSha})");
    }

    /// <summary>A second version beside the first, the alias swapped to it and back while the service keeps answering.</summary>
    private static async Task<Finding> RollbackAsync(ReviewContext c)
    {
        var stack = c.Service.Stack;
        await c.Parts.Corpus.BuildAsync(stack.Catalog, "v2-rebuild", IndexSettings.Shipped);
        var question = c.Question("G001");
        var clock = Stopwatch.StartNew();
        stack.Catalog.Swap(AssistantStack.AliasName, "v2-rebuild", AssistantStack.QueryEncoder, DateTimeOffset.UtcNow);
        var swapped = clock.Elapsed.TotalMilliseconds;
        var after = await c.AskAsync(question.Question, People.Tomas);
        clock.Restart();
        stack.Catalog.Rollback(AssistantStack.AliasName, AssistantStack.QueryEncoder, DateTimeOffset.UtcNow);
        var rolled = clock.Elapsed.TotalMilliseconds;
        var back = await c.AskAsync(question.Question, People.Tomas);
        var ok = after.IndexVersion == "v2-rebuild" && back.IndexVersion == "v1-shipped";
        return new(ok ? CheckStatus.Pass : CheckStatus.Fail,
            Invariant($"swap {swapped:F1} ms, next answer from {after.IndexVersion}; rollback {rolled:F1} ms, next from {back.IndexVersion}"));
    }

    private static async Task<Finding> DegradedAsync(ReviewContext c)
    {
        await using var service = c.Deploy("degraded", new Dictionary<string, string?> { ["AI:Provider"] = "Scripted" },
            s => s.AddSingleton(new DenseDecorator(dense => new FailingRetriever(dense))));
        var reply = (await service.AskAsync(c.Question("G003").Question, People.Kwame)).Reply!;
        var ok = reply.Status is "answered" or "partial" && reply.Degraded.Count > 0;
        return new(ok ? CheckStatus.Pass : CheckStatus.Fail, $"embedding model failing: G003 {reply.Status}, degraded: {string.Join("; ", reply.Degraded)}");
    }

    private static async Task<Finding> ModelDownAsync(ReviewContext c)
    {
        await using var service = c.Deploy("model-down", new Dictionary<string, string?> { ["AI:Provider"] = "Scripted" },
            s => s.AddKeyedSingleton<IChatClient>("answer", new ModelCircuitBreaker(new FailingChatClient())));
        var reply = (await service.AskAsync(c.Question("G001").Question, People.Tomas)).Reply!;
        var ok = reply.Status == "not_found" && reply.Sources.Count > 0 && reply.Degraded.Contains("model unavailable");
        return new(ok ? CheckStatus.Pass : CheckStatus.Fail, $"answer model failing: {reply.Status}, {reply.Sources.Count} sources listed instead of an answer");
    }

    private static Task<Finding> LatencyAsync(ReviewContext c)
    {
        var answered = c.Answers.Where(a => a.Model != "none" && !a.Cached).Select(a => a.TotalMs).Order().ToList();
        if (!c.LiveModel || answered.Count == 0) return NotRun("needs a model: run the review with --model llama3.2");
        double At(double p) => answered[Math.Min(answered.Count - 1, (int)Math.Ceiling(p * answered.Count) - 1)];
        var within = answered.Count(ms => ms <= 15_000);
        return Task.FromResult(new Finding(At(0.95) <= 15_000 ? CheckStatus.Pass : CheckStatus.Fail,
            Invariant($"{answered.Count} answers from {c.Model} on the CPU: p50 {At(0.5) / 1000:F1} s, p95 {At(0.95) / 1000:F1} s; {within} within 15 s; {c.Resilience.Timeouts} first-token timeouts retried")));
    }

    private static Task<Finding> TelemetryAsync(ReviewContext c)
    {
        // One trace per request; an answer's trace holds retrieval and assembly, and the model and validation when it ran.
        var traces = c.Spans.GroupBy(s => s.TraceId).Where(t => t.Any(s => s.DisplayName == "ask")).ToList();
        static bool Has(IEnumerable<Activity> trace, string name) => trace.Any(s => s.DisplayName == name);
        var retrieved = traces.Count(t => Has(t, "retrieve live") && Has(t, "assemble"));
        var generated = traces.Count(t => Has(t, "answer") && Has(t, "validate") && t.Any(s => s.GetTagItem("gen_ai.operation.name") is not null));
        var content = c.Spans.Count(s => s.TagObjects.Any(t => t.Key is "gen_ai.input.messages" or "gen_ai.output.messages"));
        var ok = traces.Count > 0 && retrieved == traces.Count && generated > 0 && content == 0;
        return Task.FromResult(new Finding(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"{traces.Count} answer traces, {retrieved} with retrieval spans, {generated} with model spans; {content} spans with messages"));
    }
}

/// <summary>An embedding source that fails the way a down service does.</summary>
public sealed class FailingRetriever(IRetriever inner) : IRetriever
{
    public string Name => inner.Name;

    public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("embedding service unavailable");
}

/// <summary>A model endpoint that refuses every call.</summary>
public sealed class FailingChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("model endpoint unavailable");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new HttpRequestException("model endpoint unavailable");
#pragma warning disable CS0162 // an iterator needs a yield
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
