using System.Text.Json;
using System.Text.Json.Nodes;
using Ch25.Knowledge.Api;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Governance;

namespace Ch25.Readiness.Cli;

/// <summary>Records, the registry, erasure and personal data, checked against what the review itself made the service do.</summary>
public static class GovernanceChecks
{
    public static IReadOnlyList<Check> All { get; } =
    [
        new("G1", "governance", "A provenance record for every answer", "23", "Knowledge Assistant team", RecordsAsync),
        new("G2", "governance", "The provenance chain verifies", "23", "Knowledge Assistant team", ChainAsync),
        new("G3", "governance", "Prompt and models in the registry", "23", "Riley Chen (approver)", RegistryAsync),
        new("G4", "governance", "Erasure reaches cached answers", "21, 23", "Knowledge Assistant team", ErasureAsync),
        new("G5", "governance", "Stores with question text have a retention rule", "16, 23", "Knowledge Assistant team", StoresAsync),
        new("G6", "governance", "Personal data in the indexed text", "23", "Privacy office", PiiAsync),
    ];

    private static Task<Finding> RecordsAsync(ReviewContext c)
    {
        var records = c.Parts.Provenance.Read().ToList();
        var complete = records.Count(r => r.IndexVersion.Length > 0 && r.PromptVersion.Length > 0 && r.Model.Length > 0 && r.Configuration == c.Parts.Configuration);
        var ok = records.Count == c.Answers.Count && complete == records.Count;
        return Task.FromResult(new Finding(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"{c.Answers.Count} answers, {records.Count} records, {complete} with index, prompt, model and configuration {c.Parts.Configuration}"));
    }

    private static Task<Finding> ChainAsync(ReviewContext c)
    {
        var log = c.Parts.Provenance;
        var broken = log.FirstBroken();
        var lines = File.ReadAllLines(log.Path);
        var copy = Path.Combine(c.Runs, "tampered.jsonl");
        var middle = lines.Length / 2;
        var record = JsonNode.Parse(lines[middle])!;
        record["status"] = record["status"]!.GetValue<string>() == "answered" ? "not_found" : "answered";
        lines[middle] = record.ToJsonString();
        File.WriteAllLines(copy, lines);
        var tampered = new ProvenanceLog(copy).FirstBroken();
        var ok = broken is null && tampered == middle + 1;
        return Task.FromResult(new Finding(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"{lines.Length} records intact; record {middle + 1} changed in a copy breaks the chain at {tampered?.ToString() ?? "nothing"}"));
    }

    private static Task<Finding> RegistryAsync(ReviewContext c)
    {
        var registry = ModelPromptRegistry.Load(ReviewContext.Data("registry.json"));
        var violations = registry.Check(c.Parts.Provenance.Read());
        var manifest = c.Service.Stack.Catalog.Manifest(c.Service.Stack.Catalog.Alias(AssistantStack.AliasName)!.Version);
        var embedding = registry.Entries.Any(e => e.Kind == "embedding" && e.Status == "approved" && e.Version == $"sha256:{manifest.EmbeddingSha}");
        if (!c.LiveModel)
        {
            return Task.FromResult(new Finding(CheckStatus.NotRun,
                $"the scripted stand-in isn't a model the registry approves: run the review with --model llama3.2; prompt {GroundingPrompt.Version} and embedding sha256:{manifest.EmbeddingSha} are {(embedding ? "approved" : "not approved")}"));
        }

        var ok = violations.Count == 0 && embedding;
        var reasons = violations.GroupBy(v => $"{v.Kind} {v.Version}: {v.Reason}").Select(g => $"{g.Key} ({g.Count()} records)");
        return Task.FromResult(new Finding(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"{violations.Count} violations{(violations.Count > 0 ? " (" + string.Join("; ", reasons) + ")" : "")}; the index's embedding model is {(embedding ? "approved" : "not approved")}"));
    }

    /// <summary>An answer cached, then its evidence document erased: the next request must recompute without it.</summary>
    private static async Task<Finding> ErasureAsync(ReviewContext c)
    {
        var q = c.Question("G017");
        var first = await c.AskAsync(q.Question, People.Grace);
        var second = await c.AskAsync(q.Question, People.Grace);
        var document = first.Sources[0].DocumentId;
        await c.Parts.Open.Cache.InvalidateDocumentAsync(document); // one HybridCache behind both answerers, so one tag reaches both
        var third = await c.AskAsync(q.Question, People.Grace);
        var ok = second.Cached && !third.Cached;
        return new(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"G017 cached on its second ask: {second.Cached}; {document} erased by its tag, the next ask cached: {third.Cached}");
    }

    /// <summary>The escalation queue (Chapter 16) keeps questions and askers as written; Chapter 23's retention rules don't cover it.</summary>
    private static Task<Finding> StoresAsync(ReviewContext c)
    {
        var path = Path.Combine(c.Runs, "service", "escalations.jsonl");
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        var fields = lines.Length > 0 ? string.Join(", ", JsonDocument.Parse(lines[0]).RootElement.EnumerateObject().Select(p => p.Name)) : "Question, Asker, Reason, Sources, At";
        return Task.FromResult(new Finding(CheckStatus.Warn,
            $"the escalation queue keeps the question and the asker's ID ({fields}) with no retention or erasure rule"));
    }

    private static async Task<Finding> PiiAsync(ReviewContext c)
    {
        var live = await c.Service.Stack.LiveAsync();
        var scanner = new PiiScanner(text => People.All.Select(p => p.Name).Where(n => text.Contains(n, StringComparison.Ordinal)));
        var findings = live.Chunks.Select(ch => scanner.Scan(ch.Chunk.Text)).ToList();
        var contacts = findings.Sum(f => f.Count(x => x.Kind is "email" or "phone"));
        var named = findings.Count(f => f.Any(x => x.Kind == "person"));
        return new(contacts == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            $"{contacts} email addresses or phone numbers in {live.Chunks.Count} chunks; {named} name Contoso staff, kept for now (Chapter 23)");
    }
}
