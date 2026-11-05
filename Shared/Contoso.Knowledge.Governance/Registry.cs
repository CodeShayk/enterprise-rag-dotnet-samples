using System.Text.Json;

namespace Contoso.Knowledge.Governance;

/// <summary>
/// One approved version of a model or a prompt: its identity (a digest or a hash of the text), what evidence approved
/// it (an evaluation run), who approved it and when, and whether it is still allowed to answer.
/// </summary>
public sealed record RegistryEntry(
    string Kind, string Name, string Version, string Status, string ApprovedBy, DateOnly ApprovedOn, string Evidence, string? Card = null);

public sealed record RegistryViolation(long Sequence, string Kind, string Version, string Reason);

/// <summary>The inventory of models and prompts that may produce answers, and the control that checks answers against it.</summary>
public sealed class ModelPromptRegistry(IReadOnlyList<RegistryEntry> entries)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<RegistryEntry> Entries { get; } = entries;

    public static ModelPromptRegistry Load(string path) => new(JsonSerializer.Deserialize<List<RegistryEntry>>(File.ReadAllText(path), Json)!);

    /// <summary>
    /// Every answer's prompt version and model must match an entry that was approved on or before the day it was
    /// shown and isn't retired; anything else was produced outside change control.
    /// </summary>
    public IReadOnlyList<RegistryViolation> Check(IEnumerable<AnswerProvenanceRecord> records)
    {
        var violations = new List<RegistryViolation>();
        foreach (var r in records)
        {
            var day = DateOnly.FromDateTime(r.At.UtcDateTime);
            foreach (var (kind, version) in new[] { ("prompt", r.PromptVersion), ("model", r.Model) })
            {
                if (version == "none") continue; // abstained before generation: no prompt or model ran
                var entry = Entries.FirstOrDefault(e => e.Kind == kind && e.Version == version);
                var reason = entry is null ? "not in the registry"
                    : entry.Status != "approved" ? $"status {entry.Status}"
                    : entry.ApprovedOn > day ? "used before approval"
                    : null;
                if (reason is not null) violations.Add(new RegistryViolation(r.Sequence, kind, version, reason));
            }
        }

        return violations;
    }
}
