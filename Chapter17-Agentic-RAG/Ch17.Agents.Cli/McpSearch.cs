using Contoso.Knowledge.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;

namespace Ch17.Agents.Cli;

/// <summary>
/// The agent's side of MCP: connect with the asker's own token, admit only reviewed tools (<see cref="ToolReview"/>)
/// and turn each search result back into citable evidence (<see cref="LedgerTool"/>).
/// </summary>
public static class McpSearch
{
    public static Uri Endpoint { get; } = new("http://localhost:5172/mcp");

    /// <summary>The server's tools this agent may use, each with the hash of the description that was reviewed.</summary>
    public static IReadOnlyDictionary<string, string> Reviewed { get; } = new Dictionary<string, string>
    {
        ["search_documents"] = "da8a802f3fe7ccff",
    };

    public static Task<McpClient> ConnectAsync(string token, CancellationToken cancellationToken = default) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }), cancellationToken: cancellationToken);

    public static async Task<IList<AITool>> ToolsAsync(McpClient client, AgentContext context, CancellationToken cancellationToken = default) =>
        ToolReview.Admit(await client.ListToolsAsync(cancellationToken: cancellationToken), Reviewed, context.Trajectory, tool => new LedgerTool(tool, context.Ledger));

    /// <summary>A person's development token for the MCP server, from this tool's user secrets (Mcp:Tokens:&lt;person id&gt;).</summary>
    public static string? Token(string personId) => Secrets.Value[$"Mcp:Tokens:{personId}"];

    private static readonly Lazy<IConfiguration> Secrets = new(() => new ConfigurationBuilder().AddUserSecrets(typeof(McpSearch).Assembly).Build());
}
