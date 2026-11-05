using System.Text.Json;
using Contoso.Knowledge.Documents;

namespace Ch06.Ingestion.Worker.Pipeline;

/// <summary>
/// The agreement with a source's content owners: which metadata every file must carry, from which vocabularies,
/// and how fresh the index must be. A file that breaks it is quarantined with the reason, not guessed at.
/// </summary>
public sealed record DataContract(string System, string Owner, TimeSpan Freshness, TimeSpan PermissionFreshness)
{
    public static IReadOnlyList<string> RequiredFields { get; } = ["title", "tenant", "documentType", "sensitivity", "allowedGroups", "owner"];

    /// <summary>Contoso's sources and the owners who signed their contracts (Chapter 4's freshness targets).</summary>
    public static IReadOnlyList<DataContract> Contoso { get; } =
    [
        new("fileshare", "Engineering document control", TimeSpan.FromHours(1), TimeSpan.FromMinutes(15)),
        new("intranet", "Intranet team", TimeSpan.FromHours(1), TimeSpan.FromMinutes(15)),
        new("sharepoint", "Library owners per site", TimeSpan.FromHours(1), TimeSpan.FromMinutes(15)),
        new("mail", "Field Services", TimeSpan.FromHours(1), TimeSpan.FromMinutes(15)),
        new("database", "Maintenance systems", TimeSpan.FromHours(1), TimeSpan.FromMinutes(15)),
    ];

    /// <summary>Checks the metadata a source reported; returns the metadata or the list of problems.</summary>
    public static (DocumentMetadata? Metadata, IReadOnlyList<string> Problems) Validate(JsonElement reported)
    {
        var problems = new List<string>();
        foreach (var field in RequiredFields)
        {
            if (!reported.TryGetProperty(field, out var value) || value.ValueKind is JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && value.GetString() == "") ||
                (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0))
            {
                problems.Add($"missing {field}");
            }
        }

        if (problems.Count > 0) return (null, problems);
        var metadata = reported.Deserialize<DocumentMetadata>(DocumentJson.Options)!;
        if (!DocumentTypes.All.Contains(metadata.DocumentType)) problems.Add($"unknown document type '{metadata.DocumentType}'");
        if (!SensitivityLabels.Ordered.Contains(metadata.Sensitivity)) problems.Add($"unknown sensitivity '{metadata.Sensitivity}'");
        if (metadata.EffectiveTo < metadata.EffectiveFrom) problems.Add("effective window ends before it starts");
        return problems.Count > 0 ? (null, problems) : (metadata, problems);
    }
}
