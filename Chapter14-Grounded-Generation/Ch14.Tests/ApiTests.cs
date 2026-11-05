using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch14.Tests;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    // The real indexes take seconds to build; a fixed retriever and the scripted model answer instead.
    public ApiTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureServices(services => services.AddSingleton<Func<Task<GroundedAnswerGenerator>>>(
                () => Task.FromResult(Fixtures.Generator(Fixtures.Scripted(), new Hit(Fixtures.TableRows(), 2, "test"))))));

    [Fact]
    public async Task The_stream_carries_the_sources_then_the_text_then_the_typed_answer()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask/stream",
            new { user = "tomas.silva", question = "What torque applies to the M12 flange bolts?" }, TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var events = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken))
            .EnumerateAsync(TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        Assert.Equal("sources", events[0].EventType);
        Assert.Contains("CP200-MAN", events[0].Data);
        Assert.All(events[1..^1], e => Assert.Equal("delta", e.EventType));
        Assert.Equal("answer", events[^1].EventType);
        Assert.Contains("\"status\":\"answered\"", events[^1].Data);
        Assert.Contains("\"verified\":true", events[^1].Data);
    }

    [Fact]
    public async Task An_unknown_user_gets_400_and_feedback_is_accepted()
    {
        using var client = _factory.CreateClient();

        using var unknown = await client.PostAsJsonAsync("/ask", new { user = "mallory", question = "What is the CEO's salary?" }, TestContext.Current.CancellationToken);
        using var feedback = await client.PostAsJsonAsync("/feedback",
            new { user = "tomas.silva", question = "M12 torque?", rating = "up", promptVersion = GroundingPrompt.Version, sources = new[] { "CP200-MAN:120-160" } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, feedback.StatusCode);
    }
}
