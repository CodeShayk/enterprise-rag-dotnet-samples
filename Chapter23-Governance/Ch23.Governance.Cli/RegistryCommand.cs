using System.Security.Cryptography;
using System.Text;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Governance;

namespace Ch23.Governance.Cli;

/// <summary>
/// The models and prompts that may produce answers, and the control that checks every provenance record against
/// them. Then two changes that skipped change control: a prompt edited in place and answers from an evaluated model.
/// </summary>
public static class RegistryCommand
{
    public static void Run()
    {
        var registry = ModelPromptRegistry.Load(Path.Combine(AppContext.BaseDirectory, "data", "registry.json"));
        Console.WriteLine($"Registry: {registry.Entries.Count} entries");
        foreach (var e in registry.Entries)
        {
            Console.WriteLine($"  {e.Kind,-9} {e.Version,-26} {e.Status,-9} {(e.ApprovedBy.Length > 0 ? $"by {e.ApprovedBy} on {e.ApprovedOn:yyyy-MM-dd}" : ""),-27} {e.Evidence}");
        }

        var records = new ProvenanceLog(ProvenanceCommand.LogPath).Read().ToList();
        Console.WriteLine($"{records.Count} provenance records checked: {registry.Check(records).Count} violations");

        // A rule added to the instructions in place: the version is computed from the text, so it changes with it.
        var edited = "grounded-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            (GroundingPrompt.Instructions + "- If a fault is urgent, give the vendor hotline.\n").ReplaceLineEndings("\n"))))[..8];
        var drifted = records.Select((r, i) => i >= records.Count - 3 ? r with { PromptVersion = edited }
            : i is 10 or 11 ? r with { Model = "llama3.2:1b@baf6a787fdff" } : r).ToList();
        Console.WriteLine($"after a prompt edit ({edited}) and two answers from the 1B model:");
        foreach (var v in registry.Check(drifted)) Console.WriteLine($"  record {v.Sequence}: {v.Kind} {v.Version}: {v.Reason}");
    }
}
