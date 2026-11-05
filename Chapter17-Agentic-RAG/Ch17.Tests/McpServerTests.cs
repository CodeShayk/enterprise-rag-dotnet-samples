using System.Security.Cryptography;
using Ch17.Knowledge.Mcp;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.ML.Tokenizers;
using ModelContextProtocol.Client;

namespace Ch17.Tests;

/// <summary>The MCP server in memory, with a test signing key in place of user-jwts and a retriever that records who asked.</summary>
public sealed class McpServerFactory : WebApplicationFactory<Program>
{
    private static readonly SymmetricSecurityKey Key = new(RandomNumberGenerator.GetBytes(32));

    public CapturingRetriever Retriever { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new DocumentSearch(Retriever, new ContextAssembler(new Calibration(0.474, -3.543), TiktokenTokenizer.CreateForEncoding("o200k_base"))));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                IssuerSigningKey = Key,
            });
        });
    }

    public static string Token(string name) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Claims = new Dictionary<string, object> { ["unique_name"] = name, ["sub"] = name },
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
    });

    public Task<McpClient> ConnectAsync(string? token, CancellationToken cancellationToken) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Server.BaseAddress, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = token is null ? null : new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, CreateClient()), cancellationToken: cancellationToken);
}

public sealed class McpServerTests(McpServerFactory factory) : IClassFixture<McpServerFactory>
{
    [Fact]
    public async Task The_tool_is_read_only_and_takes_only_a_query()
    {
        await using var client = await factory.ConnectAsync(McpServerFactory.Token("nadia.rahman"), TestContext.Current.CancellationToken);

        var tool = Assert.Single(await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("search_documents", tool.Name);
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Equal(["query"], tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task The_search_runs_as_the_tokens_owner_whatever_the_arguments_say()
    {
        await using var nadia = await factory.ConnectAsync(McpServerFactory.Token("nadia.rahman"), TestContext.Current.CancellationToken);

        await nadia.CallToolAsync("search_documents", new Dictionary<string, object?> { ["query"] = "supplier contract", ["asker"] = "grace.liu" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("nadia.rahman", factory.Retriever.Askers.Last().Id);
    }

    [Fact]
    public async Task A_caller_outside_the_directory_gets_an_error()
    {
        await using var stranger = await factory.ConnectAsync(McpServerFactory.Token("mallory"), TestContext.Current.CancellationToken);

        var result = await stranger.CallToolAsync("search_documents", new Dictionary<string, object?> { ["query"] = "torque" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_request_without_a_token_is_refused() =>
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var anonymous = await factory.ConnectAsync(null, TestContext.Current.CancellationToken);
        });
}
