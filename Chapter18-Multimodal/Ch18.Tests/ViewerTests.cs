using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ch18.Tests;

public sealed class ViewerTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Page = "/documents/CP200-MAN-2009/pages/3";

    [Fact]
    public async Task A_reader_of_the_manual_gets_the_highlighted_page()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Page}.png?box=57,378,334,15&as=tomas.silva", TestContext.Current.CancellationToken);
        var png = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
    }

    [Fact]
    public async Task The_page_marks_the_cited_region_over_the_render()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync($"{Page}?box=57,378,334,15&as=tomas.silva", TestContext.Current.CancellationToken);

        Assert.Contains("class=\"region\"", html);
        Assert.Contains("page 3 of 5", html);
    }

    [Theory]
    [InlineData("?as=daniel.mensah")]
    [InlineData("")]
    public async Task Anyone_who_may_not_read_the_manual_finds_no_page(string reader)
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Page}.png{reader}", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Page}{reader}", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_page_past_the_end_is_not_found()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/documents/CP200-MAN-2009/pages/9.png?as=tomas.silva", TestContext.Current.CancellationToken)).StatusCode);
    }
}
