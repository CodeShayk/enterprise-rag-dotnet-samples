using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contoso.Knowledge.Generation;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Agents;

/// <summary>
/// The check every tool from another system passes before a model sees it, such as an MCP server's: it must be on
/// the allow-list, and its description must be the one that was reviewed. A description is a prompt the server writes,
/// and the server can change it at any time.
/// </summary>
public static class ToolReview
{
    public static IList<AITool> Admit(IEnumerable<AIFunction> tools, IReadOnlyDictionary<string, string> reviewed, TrajectoryLog log, Func<AIFunction, AITool>? wrap = null)
    {
        var admitted = new List<AITool>();
        foreach (var tool in tools)
        {
            var problem = !reviewed.TryGetValue(tool.Name, out var hash) ? "not on the allow-list"
                : Hash(tool.Description) != hash ? $"description changed since review ({Hash(tool.Description)})" : null;
            if (problem is null) admitted.Add(wrap?.Invoke(tool) ?? tool);
            else log.Add("denied", tool.Name, problem, 0);
        }

        return admitted;
    }

    /// <summary>The first 16 hex digits of the description's SHA-256, line endings normalized.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n"))))[..16];
}

/// <summary>
/// A remote search tool whose results become evidence: the server's structured sources go into the ledger, and the
/// model sees them numbered like any local search. Their text is material for the answer, never instructions.
/// </summary>
public sealed class LedgerTool(AIFunction inner, EvidenceLedger ledger) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = JsonSerializer.SerializeToElement(await base.InvokeCoreAsync(arguments, cancellationToken), AIJsonUtilities.DefaultOptions);
        if (result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True) return "The search failed.";
        if (!result.TryGetProperty("structuredContent", out var content) || !content.TryGetProperty("sources", out var sources)) return "The search returned no sources.";
        var blocks = sources.Deserialize<List<EvidenceBlock>>(JsonSerializerOptions.Web) ?? [];

        // Returned as AIFunctionFactory returns a local tool's string, so a remote result and a local one look the same downstream.
        return JsonSerializer.SerializeToElement(blocks.Count == 0 ? "No relevant passages in the documents you can read." : EvidenceLedger.Describe(ledger.AddPassages(blocks)));
    }
}
