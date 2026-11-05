using System.Diagnostics;
using Contoso.Knowledge.Graph;

namespace Ch19.GraphRAG.Cli;

/// <summary>NER as a source of new names, and entity resolution with an audit log.</summary>
public static class Resolve
{
    private static readonly Dictionary<string, string[]> TypesOf = new()
    {
        ["PER"] = ["Person"],
        ["ORG"] = ["Supplier", "Customer"],
        ["LOC"] = ["Site"],
        ["MISC"] = [],
    };

    public static async Task RunAsync(Lab lab)
    {
        var graph = await Extract.LoadAsync(lab, model: false);
        using var ner = Lab.Ner();
        using var bge = Lab.Bge();
        var clock = Stopwatch.StartNew();
        var spans = new List<(string Doc, NamedEntity Entity, bool Known)>();
        foreach (var c in lab.Chunks)
        {
            var text = lab.Sources[c.Document.Id].Text[c.Chunk.Start..c.Chunk.End];
            foreach (var e in ner.Find(text)) spans.Add((c.Document.Id, e, Lab.Recognizer.Find(e.Text).Count > 0));
        }

        var nerMs = clock.Elapsed.TotalMilliseconds / lab.Chunks.Count;
        Console.WriteLine($"NER over {lab.Chunks.Count} chunks: {spans.Count} names ({string.Join(", ", spans.GroupBy(s => s.Entity.Label).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}), {nerMs:F0} ms a chunk");
        Console.WriteLine($"  already found by the rules: {spans.Count(s => s.Known)}; new: {spans.Count(s => !s.Known)} ({spans.Where(s => !s.Known).Select(s => s.Entity.Text).Distinct().Count()} distinct)");
        Console.WriteLine($"  new people, organizations and places: {string.Join(", ", spans.Where(s => !s.Known && s.Entity.Label != "MISC").Select(s => $"{s.Entity.Text} ({s.Entity.Label})").Distinct().Take(16))}");

        // Resolution: each new name against the gazetteer's names of the types its label allows.
        var resolver = new EntityResolver(name => bge.Embed([name])[0]);
        var known = Lab.Gazetteer.Select(g => (g.Type, g.Name)).ToList();
        var candidates = spans.Where(s => !s.Known).SelectMany(s => TypesOf[s.Entity.Label].Select(t => (Type: t, Name: s.Entity.Text.Trim()))).Distinct().ToList();
        var map = resolver.Resolve(known, candidates);
        var merged = resolver.Log.Where(d => d.Applied).ToList();
        Console.WriteLine($"\nResolution of {candidates.Select(c => c.Name).Distinct().Count()} new names against {known.Count} known: {merged.Count} merged, {resolver.Log.Count(d => !d.Applied)} refused");
        foreach (var d in resolver.Log.Take(12)) Console.WriteLine($"  {(d.Applied ? "merge " : "refuse")} \"{d.Merged}\" -> \"{d.Kept}\" ({d.Type}, {d.Method}, {d.Reason})");
        var left = candidates.Where(c => map[c] == c.Name).Select(c => c.Name).Distinct().ToList();
        Console.WriteLine($"  left as new names, for a person to review: {left.Count}: {string.Join(", ", left.Take(14))}");

        // A split is a rule: keep the first merge apart and resolve again.
        if (merged.FirstOrDefault() is { } first)
        {
            resolver.KeepApart.Add((first.Kept, first.Merged));
            resolver.Log.Clear();
            var again = resolver.Resolve(known, candidates);
            Console.WriteLine($"  after splitting \"{first.Merged}\" from \"{first.Kept}\": {resolver.Log.Count(d => d.Applied)} merged; the log says \"{resolver.Log.First(d => d.Merged == first.Merged && d.Kept == first.Kept).Reason}\"");
        }

        // The identifier trap: identifiers that fuzzy matching would merge.
        var identifiers = graph.Nodes.Where(n => n.Type is "Part" or "ChangeNotice" or "ErrorCode" or "Incident" or "Equipment").ToList();
        var traps = identifiers.SelectMany((a, i) => identifiers.Skip(i + 1).Where(b => b.Type == a.Type).Select(b => (a, b, Score: EntityResolver.JaroWinkler(a.Name, b.Name))))
            .Where(x => x.Score >= 0.92).OrderByDescending(x => x.Score).ToList();
        Console.WriteLine($"\nIdentifier pairs at Jaro-Winkler 0.92 or more, which fuzzy matching would merge: {traps.Count} of {identifiers.Count} identifiers, e.g. {string.Join(", ", traps.Where(t => t.a.Type == "Part").Take(3).Select(t => $"{t.a.Name} ~ {t.b.Name} {t.Score:F2}"))}");
    }
}
