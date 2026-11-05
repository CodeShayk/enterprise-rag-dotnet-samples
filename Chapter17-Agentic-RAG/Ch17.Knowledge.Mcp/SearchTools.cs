using System.ComponentModel;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ch17.Knowledge.Mcp;

/// <summary>The sources a search found, in the shape an MCP client can turn back into citable evidence.</summary>
public sealed record SearchResult(IReadOnlyList<EvidenceBlock> Sources);

/// <summary>
/// The MCP tool. Its only argument is the query: who is searching comes from the caller's validated bearer token
/// (the request's user), looked up in Contoso's directory, so no client and no model can search as someone else.
/// </summary>
[McpServerToolType]
public sealed class SearchTools(DocumentSearch search)
{
    [McpServerTool(Name = "search_documents", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Searches the Contoso documents the caller may read: equipment manuals, procedures, safety bulletins, asset records, change notices and service reports. Returns up to four sources.")]
    public async Task<SearchResult> SearchAsync(
        RequestContext<CallToolRequestParams> request,
        [Description("What to look for, in a few plain words with any equipment tag, model or part number.")] string query,
        CancellationToken cancellationToken = default)
    {
        var asker = People.Find(request.User?.Identity?.Name ?? "") ?? throw new McpException("The caller isn't in Contoso's directory.");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 300) throw new McpException("The query must be 1 to 300 characters.");
        return new SearchResult(await search.SearchAsync(query, asker, cancellationToken));
    }
}
