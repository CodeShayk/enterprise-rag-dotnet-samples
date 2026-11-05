using System.Text.RegularExpressions;
using Contoso.Knowledge.Generation;

namespace Contoso.Knowledge.Agents;

/// <summary>
/// The check for claims that cite query results, where an NLI model has little to read: every number and identifier in
/// the claim must appear in the source it cites, its title and query included. "2 of them were overdue", citing rows
/// that say 0, fails here in microseconds; passages keep Chapter 16's NLI verifier.
/// </summary>
public static partial class RowCheck
{
    public static IReadOnlyList<Claim> Unsupported(Answer answer) =>
        [.. answer.Claims.Where(c => answer.Evidence.FirstOrDefault(b => b.Id == c.Source) is { DocumentId: EvidenceLedger.RecordsDocument } rows
            && !ValuesOf(c.Text).IsSubsetOf(ValuesOf($"{rows.Title} {rows.Section} {rows.Text}")))];

    /// <summary>The claims that cite query results at all.</summary>
    public static int Checked(Answer answer) =>
        answer.Claims.Count(c => answer.Evidence.FirstOrDefault(b => b.Id == c.Source) is { DocumentId: EvidenceLedger.RecordsDocument });

    private static HashSet<string> ValuesOf(string text) => [.. Values().Matches(text).Select(m => m.Value.Replace(",", "").TrimEnd('.').ToLowerInvariant())];

    // Identifiers such as WO-88412, P-201 or PN-4471-C, and numbers, dates included part by part.
    [GeneratedRegex(@"\b[A-Za-z]{1,4}-\d[\w-]*|\b\d[\d,.]*\b")]
    private static partial Regex Values();
}
