using System.Text.Json;
using Contoso.Knowledge.Governance;

namespace Ch23.Governance.Tests;

public sealed class ProvenanceTests
{
    private static AnswerProvenanceRecord Record(int i, string prompt = "grounded-f04881ce", string model = "llama3.2@a80c4f17acd5") => new()
    {
        Sequence = i,
        At = new DateTimeOffset(2027, 1, 10, 8, 0, 0, TimeSpan.Zero).AddMinutes(i),
        RequestId = $"req-{i}",
        Asker = "2e6edcf6142f8cec",
        Tenant = "fluid",
        QuestionSha = AnswerProvenanceRecord.Sha($"question {i}"),
        IndexVersion = "idx-595ad0717074",
        Retriever = "hybrid, re-ranked",
        Evidence = [new EvidenceRef("SOP-FS-014", "3", 0, 400, "10af549049f1e8eb")],
        PromptVersion = prompt,
        Model = model,
        Status = "Answered",
        AnswerSha = AnswerProvenanceRecord.Sha($"answer {i}")[..16],
        Claims = [new ClaimRef("SOP-FS-014", true)],
    };

    private static ProvenanceLog Log(int count)
    {
        var log = new ProvenanceLog(Path.Combine(Directory.CreateTempSubdirectory().FullName, "provenance.jsonl"));
        for (var i = 1; i <= count; i++) log.Append(Record(i));
        return log;
    }

    [Fact]
    public void The_chain_holds_and_breaks_at_a_changed_or_removed_record()
    {
        var log = Log(5);
        Assert.Null(log.FirstBroken());
        Assert.Equal(5, log.Read().Last().Sequence);

        var lines = File.ReadAllLines(log.Path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var changed = JsonSerializer.Deserialize<AnswerProvenanceRecord>(lines[2], json)! with { Status = "NotFound" };
        File.WriteAllLines(log.Path, lines.Select((l, i) => i == 2 ? JsonSerializer.Serialize(changed, json) : l));
        Assert.Equal(3, log.FirstBroken());

        File.WriteAllLines(log.Path, lines.Where((_, i) => i != 1));
        Assert.Equal(3, log.FirstBroken());
    }

    [Fact]
    public void The_registry_flags_answers_outside_change_control()
    {
        var registry = new ModelPromptRegistry(
        [
            new("prompt", "grounding", "grounded-f04881ce", "approved", "Riley Chen", new DateOnly(2026, 11, 2), "Chapter 14 runs"),
            new("model", "llama3.2", "llama3.2@a80c4f17acd5", "approved", "Riley Chen", new DateOnly(2026, 11, 2), "Chapter 15 baseline"),
            new("model", "llama3.2 1B", "llama3.2:1b@baf6a787fdff", "evaluated", "", new DateOnly(2026, 11, 3), "Chapter 21 cascade"),
            new("prompt", "grounding, next", "grounded-0badc0de", "approved", "Riley Chen", new DateOnly(2027, 3, 1), "not yet"),
        ]);
        var violations = registry.Check(
        [
            Record(1),
            Record(2, prompt: "grounded-4fe34460"),
            Record(3, model: "llama3.2:1b@baf6a787fdff"),
            Record(4, prompt: "grounded-0badc0de"),
            Record(5, prompt: "none", model: "none"),
        ]);
        Assert.Equal(["not in the registry", "status evaluated", "used before approval"], violations.Select(v => v.Reason));
        Assert.Equal([2L, 3L, 4L], violations.Select(v => v.Sequence));
    }
}

public sealed class ErasureTests
{
    /// <summary>A store of named texts; a stubborn one keeps a copy, as a forgotten replica would.</summary>
    private sealed class TextStore(string name, Dictionary<string, string> entries, bool stubborn = false) : IErasableStore
    {
        public string Name => name;

        private bool Holds(KeyValuePair<string, string> e, ErasureRequest r) => e.Key == r.DocumentId || r.Probes.Any(p => e.Value.Contains(p, StringComparison.OrdinalIgnoreCase));

        public Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default) => Task.FromResult(entries.Count(e => Holds(e, r)));

        public Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default)
        {
            var keys = entries.Where(e => Holds(e, r)).Select(e => e.Key).Skip(stubborn ? 1 : 0).ToList();
            foreach (var k in keys) entries.Remove(k);
            return Task.FromResult(keys.Count);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2027, 2, 1, 7, 0, 0, TimeSpan.Zero);
    }

    private static readonly ErasureRequest Request = new("ER-1", "MAIL-1", "GDPR Art. 17", "privacy office", ["Pat Lee"]);

    private static Dictionary<string, string> Copies() => new() { ["MAIL-1"] = "Hi Pat", ["answer-7"] = "Pat Lee reported the trips", ["SOP-1"] = "Isolate the pump" };

    [Fact]
    public async Task Erasure_is_proven_only_when_every_store_counts_zero_afterward()
    {
        var tombstones = new TombstoneRegistry();
        var proven = await new ErasureWorkflow([new TextStore("documents", Copies()), new TextStore("answers", Copies())], new LegalHolds(), tombstones, new Clock()).RunAsync(Request, "sha-1", TestContext.Current.CancellationToken);
        Assert.True(proven.Proven);
        Assert.All(proven.Stores, s => Assert.Equal((2, 2, 0), (s.Before, s.Erased, s.After)));
        Assert.Equal("ER-1", tombstones.Blocking("MAIL-1", "other")!.RequestId);
        Assert.Equal("ER-1", tombstones.Blocking("MAIL-1-copy", "sha-1")!.RequestId);

        var unproven = await new ErasureWorkflow([new TextStore("replica", Copies(), stubborn: true)], new LegalHolds(), new TombstoneRegistry(), new Clock()).RunAsync(Request, "sha-1", TestContext.Current.CancellationToken);
        Assert.False(unproven.Proven);
        Assert.Equal(1, unproven.Stores[0].After);
    }

    [Fact]
    public async Task A_legal_hold_refuses_erasure_and_keeps_what_it_covers_past_retention()
    {
        var holds = new LegalHolds();
        holds.Place("HR-PL-2024", "LIT-2026-014");
        var refused = await new ErasureWorkflow([new TextStore("documents", new() { ["HR-PL-2024"] = "16 weeks" })], holds, new TombstoneRegistry(), new Clock())
            .RunAsync(new ErasureRequest("ER-2", "HR-PL-2024", "superseded", "HR", []), "sha-2", TestContext.Current.CancellationToken);
        Assert.Equal("refused: legal hold (LIT-2026-014)", refused.Outcome);
        Assert.Null(refused.Tombstone);

        var now = new DateTimeOffset(2027, 2, 1, 7, 0, 0, TimeSpan.Zero);
        var decisions = RetentionJob.Review(
        [
            new RetainedItem("old", "captured content", now.AddDays(-45), ["SOP-FS-014"]),
            new RetainedItem("old, held", "captured content", now.AddDays(-45), ["HR-PL-2024"]),
            new RetainedItem("recent", "captured content", now.AddDays(-5), ["HR-PL-2024"]),
        ], new Dictionary<string, TimeSpan> { ["captured content"] = TimeSpan.FromDays(30) }, holds, now);
        Assert.Equal(["erase", "held (LIT-2026-014)", "keep"], decisions.Select(d => d.Decision));
    }
}

public sealed class PiiTests
{
    [Fact]
    public void The_scanner_finds_addresses_numbers_and_names_and_masks_them()
    {
        var scanner = new PiiScanner(text => text.Contains("Pat Lee", StringComparison.Ordinal) ? ["Pat Lee"] : []);
        const string text = "On Thu, 5 Nov 2026 at 08:12, Pat Lee <pat.lee@tailspin.example> wrote: call +1 425 555 0199.";
        var found = scanner.Scan(text);
        Assert.Equal(["email", "phone", "person"], found.Select(f => f.Kind));
        Assert.Equal("On Thu, 5 Nov 2026 at 08:12, [person] <[email]> wrote: call [phone].", scanner.Minimize(text, found));
    }
}
