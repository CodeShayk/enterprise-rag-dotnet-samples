using System.Diagnostics;
using Contoso.Knowledge.Agents;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Ch20.Observability.Tests;

public sealed class AgentSpanTests
{
    /// <summary>A model that asks for one tool call, then says it's done.</summary>
    private sealed class ToolCallingClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Increment(ref _calls) == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "find_parts", new Dictionary<string, object?> { ["partNumber"] = "PN-4471-C" })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "DONE")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tool_call_is_a_span_and_its_arguments_are_content(bool sensitive)
    {
        // A source of its own: tests run in parallel, and the trace tests listen to the assistant's sources.
        const string source = "Ch20.Tests.AgentSpans";
        var spans = new List<Activity>();
        using (Sdk.CreateTracerProviderBuilder().AddSource(source).AddInMemoryExporter(spans).Build())
        {
            var client = new ChatClientBuilder(new ToolCallingClient())
                .UseFunctionInvocation()
                .UseOpenTelemetry(sourceName: source, configure: c => c.EnableSensitiveData = sensitive)
                .Build();
            var tool = AIFunctionFactory.Create((string partNumber) => $"{partNumber}: 12 in stock at Riverside", "find_parts");
            await client.GetResponseAsync("How many PN-4471-C seals are in stock?", new ChatOptions { Tools = [tool] }, TestContext.Current.CancellationToken);
        }

        var call = Assert.Single(spans, s => s.OperationName == "execute_tool find_parts");
        Assert.Equal("find_parts", call.GetTagItem("gen_ai.tool.name"));
        Assert.Equal(sensitive, call.GetTagItem("gen_ai.tool.call.arguments") is not null);
        Assert.Equal(2, spans.Count(s => s.OperationName.StartsWith("chat", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_refused_tool_is_an_event_on_the_span_it_happened_in()
    {
        using var source = new ActivitySource("test.agents");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.agents",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var log = new TrajectoryLog();
        using (var span = source.StartActivity("execute_tool find_parts"))
        {
            log.Add("denied", "find_parts", "{}", 0, outcome: "not on this person's allow-list");
            log.Add("tool", "find_parts", "{}", 3, outcome: "12 in stock");
            var e = Assert.Single(span!.Events);
            Assert.Equal("agent.denied", e.Name);
            Assert.Contains(e.Tags, t => t is { Key: "agent.step.outcome", Value: "not on this person's allow-list" });
        }
    }
}
