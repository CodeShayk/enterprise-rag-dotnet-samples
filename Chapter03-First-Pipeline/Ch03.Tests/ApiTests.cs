using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ch03.Tests;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host
            .UseSetting("AI:ScriptedChunkDelayMilliseconds", "0")
            .UseSetting("Logging:LogLevel:Default", "Warning"));

    [Fact]
    public async Task The_stream_carries_sources_then_text_then_done()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask/stream",
            new { user = "kwame.asante", question = "What does error E-217 mean on a CP-200, and how do I clear it?" },
            TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var events = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken))
            .EnumerateAsync(TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        Assert.Equal("sources", events[0].EventType);
        Assert.Contains("CP200-TRB-E217", events[0].Data);
        Assert.All(events[1..^1], e => Assert.Equal("delta", e.EventType));
        Assert.Equal("done", events[^1].EventType);
    }

    [Fact]
    public async Task An_unknown_user_gets_400()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask",
            new { user = "mallory", question = "What is the CEO's salary?" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
