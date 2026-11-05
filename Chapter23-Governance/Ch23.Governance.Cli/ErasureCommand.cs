using System.Diagnostics;
using System.Text.Json;
using Contoso.Knowledge.Governance;
using static System.FormattableString;

namespace Ch23.Governance.Cli;

/// <summary>Contoso's clock: always Monday, February 1, 2027, 07:00 UTC.</summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Pat Lee, Tailspin Water Utility's maintenance lead, asks Contoso to erase their personal data. The email thread
/// that holds it is removed from every store the assistant keeps, and the workflow counts again to prove it. Then the
/// same email is offered again by the mail connector, the 2024 parental leave policy is asked for while under a legal
/// hold, and the content store's retention runs.
/// </summary>
public static class ErasureCommand
{
    public static async Task RunAsync(Lab lab)
    {
        // Work on copies of the run files, so the command can run again from the same starting point.
        var work = Directory.CreateDirectory(Path.Combine(Lab.Runs, "erasure")).FullName;
        foreach (var file in new[] { "provenance.jsonl", "content.jsonl", "llm-recordings.jsonl" }) File.Copy(Path.Combine(Lab.Runs, file), Path.Combine(work, file), overwrite: true);
        var answers = File.ReadLines(Path.Combine(work, "content.jsonl")).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<ContentEntry>(l, ProvenanceCommand.Json)!).ToList();
        var embeddings = await lab.Passages.GenerateAsync(lab.Chunks.Select(c => c.Chunk.IndexText));
        var provenance = new ProvenanceStore(Path.Combine(work, "provenance.jsonl"));
        IReadOnlyList<IErasableStore> stores =
        [
            new DocumentStore(lab.Documents),
            new KeywordIndexStore(lab.Chunks),
            new VectorIndexStore(lab.Chunks.ToDictionary(c => c.Ordinal, c => (c.Document.Id, embeddings[c.Ordinal].Vector))),
            new GraphStore(await lab.GraphAsync(), lab.Entries.ToDictionary(e => e.Key, e => e.Value.Version)),
            await AnswerCacheStore.CreateAsync(2, answers),
            new ContentStore(Path.Combine(work, "content.jsonl")),
            new RecordingStore(Path.Combine(work, "llm-recordings.jsonl")),
            new GoldenSetStore(lab.Questions),
            provenance,
        ];
        var holds = new LegalHolds();
        holds.Place("HR-PL-2024", "LIT-2026-014");
        var tombstones = new TombstoneRegistry();
        var workflow = new ErasureWorkflow(stores, holds, tombstones, new FixedClock(Lab.Today));

        const string email = "MAIL-2026-1107-PUMPINGSTA";
        var request = new ErasureRequest("ER-2027-003", email, "GDPR Art. 17, request from the data subject", "privacy office", ["Pat Lee", "pat.lee@tailspin.example", "duplex strainer so your operators", "Sand from the raw-water supply blocks that strainer"]);
        var clock = Stopwatch.StartNew();
        var certificate = await workflow.RunAsync(request, lab.Entries[email].Sha256);
        clock.Stop();
        Console.WriteLine($"{request.Id}: {email}, {request.Basis}; probes: {string.Join(", ", request.Probes)}");
        Console.WriteLine($"{"Store",-32} {"before",6} {"erased",7} {"after",6}");
        foreach (var s in certificate.Stores) Console.WriteLine($"{s.Store,-32} {s.Before,6} {s.Erased,7} {s.After,6}");
        Console.WriteLine(Invariant($"{certificate.Outcome} in {clock.Elapsed.TotalSeconds:F1} s; proven: {(certificate.Proven ? "yes" : "no")}; tombstone {certificate.Tombstone!.RequestId} at {certificate.Tombstone.ErasedAt:yyyy-MM-dd HH:mm}Z, content {certificate.Tombstone.ContentSha[..12]}"));
        Console.WriteLine($"provenance records whose evidence included it: {provenance.Citing(email)}, kept (identifiers and hashes, no text)");

        var offered = lab.Entries[email];
        Console.WriteLine($"the mail connector offers {offered.Id} again (same file): {(tombstones.Blocking(offered.Id, offered.Sha256) is { } t ? $"refused by the tombstone of {t.RequestId}" : "indexed")}");

        var held = await workflow.RunAsync(new ErasureRequest("ER-2027-004", "HR-PL-2024", "superseded policy, request from HR", "Daniel Mensah", []), lab.Entries["HR-PL-2024"].Sha256);
        Console.WriteLine($"ER-2027-004: HR-PL-2024, {held.Outcome}; provenance records whose evidence included it: {provenance.Citing("HR-PL-2024")}, preserved with it");

        var remaining = File.ReadLines(Path.Combine(work, "content.jsonl")).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<ContentEntry>(l, ProvenanceCommand.Json)!).ToList();
        var decisions = RetentionJob.Review(remaining.Select(e => new RetainedItem(e.RequestId, "captured content", e.At, e.Documents)),
            new Dictionary<string, TimeSpan> { ["captured content"] = TimeSpan.FromDays(30) }, holds, Lab.Today);
        Console.WriteLine($"captured content, 30-day retention on {Lab.Today:yyyy-MM-dd}: {decisions.Count} entries; " +
            string.Join(", ", decisions.GroupBy(d => d.Decision).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
    }
}
