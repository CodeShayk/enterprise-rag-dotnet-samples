using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Retrieval;
using Contoso.Knowledge.Security;
using static System.FormattableString;

namespace Ch22.Security.Cli;

/// <summary>Permissions in retrieval: trimming inside the search against filtering after it, permission sync, and poisoned documents.</summary>
public static class RetrievalCommands
{
    /// <summary>Everyone's groups at once: the asker a post-filtering system searches as before it drops what the real asker can't read.</summary>
    private static readonly Person Everyone = new("search-service", "Search service", "", "corporate",
        People.All.SelectMany(p => p.Groups).Concat(["motion", "service", "procurement", "hr", "people-managers"]).ToHashSet(StringComparer.Ordinal));

    public static async Task TrimAsync(Lab lab)
    {
        var retriever = await lab.RetrieverAsync(lab.Chunks, rerank: false);
        var dev = lab.Split("dev").ToList();
        Console.WriteLine($"Each of {dev.Count} dev questions asked by each person: hybrid retrieval, top 4");
        Console.WriteLine($"{"Asker",-14} {"trimmed: unreadable",20} {"unfiltered top 4 held unreadable",33} {"post-filter < 4",16} {"post-filter 0",14}");
        foreach (var person in People.All)
        {
            var (leaks, exposed, short4, empty) = (0, 0, 0, 0);
            foreach (var q in dev)
            {
                var trimmed = (await retriever.RetrieveAsync(new RetrievalRequest(q.Question, person, 4))).Hits;
                leaks += trimmed.Count(h => !person.CanRead(h.Chunk.Document));
                var unfiltered = (await retriever.RetrieveAsync(new RetrievalRequest(q.Question, Everyone, 4))).Hits;
                var post = unfiltered.Where(h => person.CanRead(h.Chunk.Document)).ToList();
                if (post.Count < unfiltered.Count) exposed++;
                if (post.Count < 4) short4++;
                if (post.Count == 0) empty++;
            }

            Console.WriteLine($"{person.Id,-14} {leaks,20} {exposed,33} {short4,16} {empty,14}");
        }
    }

    /// <summary>
    /// A permission change after indexing: the Fabrikam Seals contract was shared with every employee by mistake and is
    /// corrected to procurement only. The stale index, the check against live permissions, and a permission-only update.
    /// </summary>
    public static async Task SyncAsync(Lab lab)
    {
        const string contract = "SC-2024-118";
        var misShared = lab.Documents[contract] with { AllowedGroups = ["employees"] };
        var chunks = Replace(lab.Chunks, misShared);
        var acls = new AclStore(lab.Documents.Values.Select(d => d.Id == contract ? misShared : d));

        var clock = Stopwatch.StartNew();
        var index = await lab.RetrieverAsync(chunks, rerank: false);
        var built = clock.Elapsed.TotalMilliseconds;
        var embedded = lab.Passages.Computed;
        var question = lab.Questions.Single(q => q.Id == "G017").Question;
        async Task<IReadOnlyList<Hit>> Ask(IRetriever r) => (await r.RetrieveAsync(new RetrievalRequest(question, People.Tomas, 4))).Hits;
        string Has(IEnumerable<Hit> hits) => hits.Any(h => h.Chunk.Document.Id == contract) ? "in Tomás's hits" : "not in Tomás's hits";

        Console.WriteLine($"{contract} shared with every employee by mistake; Tomás asks G017");
        Console.WriteLine(Invariant($"  index built from scratch ({embedded} chunks embedded, {built:F0} ms): {contract} {Has(await Ask(index))}"));
        var change = acls.Set(contract, "procurement");
        Console.WriteLine($"ACL change {change.Version}: {contract} -> procurement only");
        var stale = await Ask(index);
        var (allowed, blocked) = PermissionCheck.Verify(People.Tomas, stale, acls);
        Console.WriteLine($"  stale index: {contract} {Has(stale)}; verified against live ACLs: {blocked.Count} blocked, {allowed.Count} passed");

        clock.Restart();
        var updated = Replace(chunks, acls.Apply(misShared));
        var reindexed = await lab.RetrieverAsync(updated, rerank: false);
        Console.WriteLine(Invariant($"  permission-only update ({lab.Passages.Computed - embedded} chunks embedded, {clock.Elapsed.TotalMilliseconds:F0} ms): {contract} {Has(await Ask(reindexed))}"));

        var (checkedHits, leaks) = (0, 0);
        foreach (var person in People.All)
        {
            foreach (var q in lab.Split("dev"))
            {
                var hits = (await reindexed.RetrieveAsync(new RetrievalRequest(q.Question, person, 4))).Hits;
                checkedHits += hits.Count;
                leaks += hits.Count(h => !acls.CanRead(person, h.Chunk.Document.Id));
            }
        }

        Console.WriteLine($"leakage test after the update: {People.All.Count} people x {lab.Split("dev").Count()} dev questions, {checkedHits} hits, {leaks} unreadable");
    }

    private static List<IndexedChunk> Replace(IEnumerable<IndexedChunk> chunks, SourceDocument document) =>
        [.. chunks.Select(c => c.Document.Id == document.Id ? c with { Document = document } : c)];

    /// <summary>How often each poisoned document reaches the evidence for the dev questions, with and without a trust filter.</summary>
    public static async Task PoisonAsync(Lab lab)
    {
        var poisoned = lab.Attacks.Where(a => a.Document is not null).ToList();
        var chunks = lab.WithPoison(poisoned);
        var retriever = await lab.RetrieverAsync(chunks);
        var trusted = chunks.Where(c => TrustTiers.Of(c) >= SourceTier.Managed).Select(c => c.DocumentType).Distinct().ToList();
        var dev = lab.Split("dev").ToList();
        var counts = poisoned.ToDictionary(a => a.Document!, _ => (Open: 0, Filtered: 0));
        var lostEvidence = 0;
        foreach (var q in dev)
        {
            var asker = People.Find(q.Asker)!;
            var open = (await retriever.RetrieveAsync(new RetrievalRequest(q.Question, asker, 4))).Hits;
            var filtered = (await retriever.RetrieveAsync(new RetrievalRequest(q.Question, asker, 4, new RetrievalFilter { DocumentTypes = trusted }))).Hits;
            foreach (var a in poisoned)
            {
                var c = counts[a.Document!];
                counts[a.Document!] = (c.Open + (open.Any(h => h.Chunk.Document.Id == a.Document) ? 1 : 0), c.Filtered + (filtered.Any(h => h.Chunk.Document.Id == a.Document) ? 1 : 0));
            }

            var evidence = Contoso.Knowledge.Evaluation.GoldenSet.Locate(q, lab.Documents).Select(s => s.Doc).ToHashSet(StringComparer.Ordinal);
            if (lab.Chunks.Any(c => evidence.Contains(c.Document.Id) && TrustTiers.Of(c) < SourceTier.Managed)) lostEvidence++;
        }

        Console.WriteLine($"{poisoned.Count} poisoned documents added to {lab.Chunks.Count} chunks; {dev.Count} dev questions, hybrid re-ranked, top 4");
        Console.WriteLine($"{"Document",-26} {"tier",-15} {"in top 4",9} {"with the trust filter",22}");
        foreach (var a in poisoned)
        {
            var chunk = chunks.First(c => c.Document.Id == a.Document);
            Console.WriteLine($"{a.Document,-26} {TrustTiers.Of(chunk),-15} {counts[a.Document!].Open,9} {counts[a.Document!].Filtered,22}");
        }

        Console.WriteLine($"trust filter: types {string.Join(", ", trusted.Order())}; dev questions whose labeled evidence is in a lower tier: {lostEvidence}");
    }
}
