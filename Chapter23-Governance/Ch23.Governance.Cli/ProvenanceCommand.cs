using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Governance;
using Contoso.Knowledge.Observability;
using static System.FormattableString;

namespace Ch23.Governance.Cli;

/// <summary>A captured question and answer, kept apart from the provenance record under its own retention.</summary>
public sealed record ContentEntry(string RequestId, DateTimeOffset At, string Question, string Answer, IReadOnlyList<string> Documents);

/// <summary>A question someone asked outside the golden set, at a time on Contoso's calendar.</summary>
public sealed record Traffic(string Asker, DateTimeOffset At, string Question);

/// <summary>
/// A provenance record for every answer of the shipped pipeline over the 60 days before Contoso's "today" (the dev
/// questions and a few of Kwame's own), then the checks an auditor would run on the log: the chain, a changed record,
/// a deleted record, and who saw what.
/// </summary>
public static class ProvenanceCommand
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string LogPath => Path.Combine(Lab.Runs, "provenance.jsonl");

    public static string ContentPath => Path.Combine(Lab.Runs, "content.jsonl");

    public static async Task RunAsync(Lab lab)
    {
        Directory.CreateDirectory(Lab.Runs);
        File.Delete(LogPath);
        File.Delete(ContentPath);
        var log = new ProvenanceLog(LogPath);
        var key = RandomNumberGenerator.GetBytes(32);
        var chat = Lab.AnswerChat();
        var generator = lab.Generator(await lab.RetrieverAsync(lab.Chunks), chat);
        var (index, model) = (Lab.IndexVersion(lab.Chunks), $"llama3.2@{Lab.OllamaDigest("llama3.2")}");
        // The dev questions spread over the 60 days before "today", and Kwame's own questions about a customer visit.
        var dev = lab.Split("dev").ToList();
        var traffic = JsonSerializer.Deserialize<List<Traffic>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "traffic.v1.json")), Json)!;
        var timeline = dev.Select((q, i) => (At: Lab.Today.AddDays(-60) + TimeSpan.FromDays(60.0 * i / dev.Count), q.Question, q.Asker))
            .Concat(traffic.Select(t => (t.At, t.Question, t.Asker))).OrderBy(x => x.At).ToList();
        var appending = new Stopwatch();
        for (var i = 0; i < timeline.Count; i++)
        {
            var (at, question, askerId) = timeline[i];
            var asker = People.Find(askerId)!;
            var answer = await Lab.AnswerAsync(generator, question, asker);
            var record = Record($"req-{at:yyyyMMdd}-{i + 1:D4}", at, question, asker, answer, lab, key, index, model);
            appending.Start();
            log.Append(record);
            appending.Stop();
            await File.AppendAllTextAsync(ContentPath, JsonSerializer.Serialize(
                new ContentEntry(record.RequestId, at, question, answer.Text, [.. record.Evidence.Select(e => e.DocumentId).Distinct()]), Json) + "\n");
        }

        var records = log.Read().ToList();
        var verify = Stopwatch.StartNew();
        var broken = log.FirstBroken();
        verify.Stop();
        Console.WriteLine(Invariant($"{records.Count} answers ({chat.Replayed} model calls replayed, {chat.Recorded} live), {records.Count} provenance records, {new FileInfo(LogPath).Length / records.Count:N0} bytes a record"));
        Console.WriteLine(Invariant($"  appending took {appending.Elapsed.TotalMilliseconds * 1000 / records.Count:F0} µs a record; verifying the chain {verify.Elapsed.TotalMilliseconds:F1} ms: {(broken is null ? "intact" : $"broken at {broken}")}"));

        var sample = records.First(r => r.Evidence.Any(e => e.DocumentId == "MAIL-2026-1107-PUMPINGSTA"));
        Console.WriteLine($"Record {sample.Sequence}:");
        Console.WriteLine(JsonSerializer.Serialize(sample, new JsonSerializerOptions(Json) { WriteIndented = true }));

        // The auditor's checks on copies of the log: one record changed, one record removed.
        var lines = File.ReadAllLines(LogPath);
        var changed = Path.Combine(Lab.Runs, "provenance-changed.jsonl");
        File.WriteAllLines(changed, lines.Select((l, i) => i != 41 ? l : JsonSerializer.Serialize(
            records[i] with { Status = records[i].Status == "Answered" ? "NotFound" : "Answered" }, Json)));
        var removed = Path.Combine(Lab.Runs, "provenance-removed.jsonl");
        File.WriteAllLines(removed, lines.Where((_, i) => i != 16));
        Console.WriteLine($"record 42's status changed in a copy: chain broken at {new ProvenanceLog(changed).FirstBroken()}");
        Console.WriteLine($"record 17 removed from a copy: chain broken at {new ProvenanceLog(removed).FirstBroken()}");

        foreach (var doc in new[] { "MAIL-2026-1107-PUMPINGSTA", "HR-PL-2024" })
        {
            var cited = records.Where(r => r.Evidence.Any(e => e.DocumentId == doc)).ToList();
            var people = cited.Select(r => r.Asker).Distinct().Count();
            Console.WriteLine($"answers whose evidence included {doc}: {cited.Count}, shown to {people} {(people == 1 ? "person" : "people")} ({string.Join(", ", cited.Select(r => r.RequestId))})");
        }
    }

    public static AnswerProvenanceRecord Record(
        string requestId, DateTimeOffset at, string question, Person asker, Answer answer, Lab lab, byte[] key, string index, string model) => new()
    {
        At = at,
        RequestId = requestId,
        Asker = TelemetryContent.Pseudonym(asker.Id, key),
        Tenant = asker.Tenant,
        QuestionSha = AnswerProvenanceRecord.Sha(question),
        IndexVersion = index,
        Retriever = "hybrid, re-ranked",
        Evidence = [.. answer.Evidence.Select(b => new EvidenceRef(
            b.DocumentId, lab.Entries[b.DocumentId].Version, b.Segments.Min(s => s.Start), b.Segments.Max(s => s.End), AnswerProvenanceRecord.Sha(b.Text)[..16]))],
        PromptVersion = answer.PromptVersion,
        Model = answer.PromptVersion == "none" ? "none" : model,
        Status = answer.Status.ToString(),
        AnswerSha = AnswerProvenanceRecord.Sha(answer.Text)[..16],
        Claims = [.. answer.Claims.Select(c => new ClaimRef(c.DocumentId ?? "", c.QuoteFound))],
    };
}
