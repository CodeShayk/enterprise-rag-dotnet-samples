using System.Text;
using Ch22.Security.Cli;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Security;

// Commands (from the sample-code folder):
//   identity    the search API behind JWT bearer tokens shaped like Entra ID's: claims, group expansion, overage
//   trim        security trimming inside the search against filtering after it, every person x every dev question
//   sync        a permission change after indexing: the stale index, verification, a permission-only update
//   tenants     one table for every tenant, isolated by PostgreSQL row-level security (needs Docker)
//   inject      poisoned documents and direct injections against the shipped pipeline (llama3.2; replays once recorded)
//   exfil       answers with images and links, rendered as Markdown and through the allow-list
//   poison      how often poisoned documents reach the evidence, with and without a trust filter
//   supply      every model file against the SHA-256 its catalog entry recorded, and the catalog as a CycloneDX ML-BOM
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "trim";
if (command == "exfil")
{
    AttackCommands.Exfiltrate();
    return;
}

if (command == "supply")
{
    var root = EmbeddingModels.Root;
    var checks = ModelProvenance.Verify(Path.Combine(root, "models.json"), root);
    Console.WriteLine($"{checks.Count} models in models.json, checked against their recorded SHA-256:");
    foreach (var c in checks) Console.WriteLine($"  {c.Name,-34} {c.Status}");
    var bom = Path.GetFullPath(Path.Combine(root, "..", "runs", "ch22", "ml-bom.cdx.json"));
    Console.WriteLine($"ML-BOM: {MlBom.Write(Path.Combine(root, "models.json"), bom)} components, CycloneDX 1.6 -> {Path.GetRelativePath(Path.Combine(root, ".."), bom)}");
    if (checks.Any(c => c.Status.StartsWith("MISMATCH", StringComparison.Ordinal))) Environment.ExitCode = 1; // a pipeline step fails here
    return;
}

var lab = await Lab.LoadAsync();
await (command switch
{
    "identity" => IdentityCommand.RunAsync(lab),
    "trim" => RetrievalCommands.TrimAsync(lab),
    "sync" => RetrievalCommands.SyncAsync(lab),
    "tenants" => TenantsCommand.RunAsync(lab),
    "inject" => AttackCommands.InjectAsync(lab),
    "poison" => RetrievalCommands.PoisonAsync(lab),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
