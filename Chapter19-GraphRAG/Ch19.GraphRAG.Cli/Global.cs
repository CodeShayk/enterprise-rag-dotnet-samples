using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Graph;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;

namespace Ch19.GraphRAG.Cli;

/// <summary>A global question and the themes a complete answer covers (regular expressions).</summary>
public sealed record GlobalQuestion(string Id, string Asker, string Question, IReadOnlyList<(string Name, string Pattern)> Themes);

/// <summary>Communities, summaries made on demand and map-reduce answers; and keeping the graph fresh.</summary>
public static partial class Global
{
    public static IReadOnlyList<GlobalQuestion> Questions { get; } =
    [
        new("G1", "kwame.asante", "What problems have field service visits found at customer sites, and what fixed them?",
            [("seal-water strainer and E-217", "strainer|E-217"), ("seal wear", "seal"), ("wear ring clearance", "wear ring|clearance"), ("drive fault F002", "F002|ramp"), ("gearbox oil", "oil")]),
        new("G2", "tomas.silva", "What engineering changes have been made to Contoso's pump parts, and why?",
            [("seal PN-4471-C", "PN-4471-C|silicon"), ("seal-water temperature", "temperature|60"), ("wear ring PN-2840-B", "PN-2840-B|wear ring"), ("dry running or clearance", "dry run|clearance|efficien")]),
    ];

    /// <summary>One community: its ID, its nodes and the chunks its edges were read from.</summary>
    private sealed record Community(int Id, IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges);

    public static async Task RunAsync(Lab lab)
    {
        var graph = await Extract.LoadAsync(lab, model: false);
        var chat = Lab.GraphChat();
        var all = Communities(graph);
        Console.WriteLine($"Louvain on the whole graph: {all.Count} communities with an edge, modularity {Louvain.Modularity(graph, Louvain.Detect(graph)):F2}; largest: " +
            string.Join("; ", all.OrderByDescending(c => c.Nodes.Count).Take(3).Select(c => $"{c.Nodes.Count} nodes around {string.Join(", ", Hubs(c).Take(3))}")));

        // Eager: a summary for every community of the whole graph at indexing time.
        var eager = Stopwatch.StartNew();
        double eagerModel = 0;
        foreach (var c in all) eagerModel += (await SummarizeAsync(lab, chat, graph, c)).Ms;
        Console.WriteLine($"Summarizing every community when the graph is built: {all.Count} model calls, {eagerModel / 1000:F0} s of model time; and again for every permission set that sees a different graph\n");

        foreach (var q in Questions)
        {
            var asker = People.Find(q.Asker)!;
            var readable = lab.Chunks.Where(c => asker.CanRead(c.Document)).Select(c => c.Document.Id).ToHashSet();
            var view = graph.View(m => readable.Contains(m.DocumentId));
            var communities = Communities(view);

            // Lazy: rank the asker's communities against the question, summarize the top three, map, then reduce.
            var terms = Terms(q.Question);
            var chosen = communities.Select(c => (c, Score: Terms(Text(lab, c)).Count(terms.Contains))).OrderByDescending(x => x.Score).Take(3).Select(x => x.c).ToList();
            double ms = 0;
            var partials = new List<string>();
            foreach (var c in chosen)
            {
                var (summary, summaryMs) = await SummarizeAsync(lab, chat, view, c);
                var (partial, mapMs) = await AskAsync(chat, $"Community summary:\n{summary}\n\nQuestion: {q.Question}\nAnswer from the summary only, in two or three sentences. If it has nothing on the question, answer NONE.");
                ms += summaryMs + mapMs;
                if (!partial.Contains("NONE", StringComparison.Ordinal)) partials.Add(partial);
            }

            var (final, reduceMs) = await AskAsync(chat, $"Partial answers from different parts of the knowledge graph:\n- {string.Join("\n- ", partials)}\n\nQuestion: {q.Question}\nCombine them into one answer of at most six sentences. Use only what the partial answers say.");
            ms += reduceMs;
            var single = await (await lab.GeneratorAsync(await lab.RetrieverAsync("hybrid", graph), Lab.AnswerChat())).AnswerAsync(q.Question, asker);
            Console.WriteLine($"{q.Id} {asker.Name}: {q.Question}");
            Console.WriteLine($"  asker's graph: {view.Edges.Count} edges, {communities.Count} communities; chosen: {string.Join("; ", chosen.Select(c => string.Join(", ", Hubs(c).Take(2))))}");
            Console.WriteLine($"  map-reduce over 3 summaries: {chosen.Count * 2 + 1} model calls, {ms / 1000:F0} s; themes {Covered(q, final)}");
            Console.WriteLine($"  single pass (Chapter 14): {single.Timings.TotalMs / 1000:F0} s; themes {Covered(q, single.Text)}");
            if (Environment.GetEnvironmentVariable("CH19_DEBUG") == "1") Console.WriteLine($"    map-reduce: {final.ReplaceLineEndings(" ")}\n    single: {single.Text.ReplaceLineEndings(" ")}");
        }
    }

    private static string Covered(GlobalQuestion q, string answer)
    {
        var hit = q.Themes.Where(t => Regex.IsMatch(answer, t.Pattern, RegexOptions.IgnoreCase)).Select(t => t.Name).ToList();
        return $"{hit.Count}/{q.Themes.Count} ({string.Join(", ", hit)})";
    }

    private static List<Community> Communities(KnowledgeGraph graph)
    {
        var membership = Louvain.Detect(graph);
        return [.. membership.GroupBy(m => m.Value).Select(g => new Community(g.Key, [.. g.Select(m => graph.Find(m.Key)!)],
            [.. graph.Edges.Where(e => membership[e.Key.From] == g.Key && membership[e.Key.To] == g.Key)])).Where(c => c.Edges.Count > 0)];
    }

    private static IEnumerable<string> Hubs(Community c) =>
        c.Nodes.OrderByDescending(n => c.Edges.Count(e => e.Key.From == n.Id || e.Key.To == n.Id)).Select(n => n.Name);

    /// <summary>A community as text: its edges and the passages they were read from, at most 6,000 characters.</summary>
    private static string Text(Lab lab, Community c)
    {
        var text = new StringBuilder(string.Join('\n', c.Edges.Select(e => $"{e.Key.From} {e.Key.Relation} {e.Key.To}")));
        foreach (var m in c.Edges.SelectMany(e => e.Mentions).DistinctBy(m => (m.DocumentId, m.Start)))
        {
            if (text.Length > 6000) break;
            text.Append($"\n[{m.DocumentId}] {lab.Sources[m.DocumentId].Text[m.Start..Math.Min(m.End, lab.Sources[m.DocumentId].Text.Length)]}");
        }

        return text.ToString()[..Math.Min(6000, text.Length)];
    }

    private static Task<(string Text, double Ms)> SummarizeAsync(Lab lab, IChatClient chat, KnowledgeGraph graph, Community c) =>
        AskAsync(chat, $"These facts and passages form one community of Contoso's maintenance knowledge graph.\n\n{Text(lab, c)}\n\nSummarize what this community is about in at most five sentences: the equipment, parts, suppliers, customers and events, and what happened. Use only the text above.");

    private static async Task<(string Text, double Ms)> AskAsync(IChatClient chat, string prompt)
    {
        var clock = Stopwatch.StartNew();
        var response = await chat.GetResponseAsync(prompt, new ChatOptions { Temperature = 0, MaxOutputTokens = 250 });
        var ms = response.AdditionalProperties?.TryGetValue("recorded.totalMs", out var recorded) == true && recorded is not null
            ? Convert.ToDouble(recorded, System.Globalization.CultureInfo.InvariantCulture) : clock.Elapsed.TotalMilliseconds;
        return (response.Text.Trim(), ms);
    }

    private static HashSet<string> Terms(string text) =>
        [.. Words().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 3 && !Stop.Contains(w))];

    private static readonly HashSet<string> Stop = ["what", "have", "been", "made", "that", "them", "with", "from", "this", "were", "which", "they", "their", "contoso"];

    [GeneratedRegex(@"[\p{L}\p{N}-]+")]
    private static partial Regex Words();

    /// <summary>
    /// Freshness: the graph built with SOP-FS-014 version 2, then updated to version 3 by removing version 2's
    /// mentions (and whatever they alone supported) and adding version 3, against rebuilding everything.
    /// </summary>
    public static async Task FreshAsync(Lab lab)
    {
        var builder = new GraphBuilder(Lab.Recognizer);
        var options = new ExtractionOptions();
        var old = GeneratedCorpus.Register().Single(e => e.Id == "SOP-FS-014" && e.Version == "2");
        var (oldSource, _) = await Lab.OpenAsync(old);
        var oldChunks = await Lab.Chunker.SplitAsync(oldSource);
        var graph = new KnowledgeGraph();
        foreach (var (id, source) in lab.Sources) await builder.AddAsync(graph, id == "SOP-FS-014" ? oldSource : source, id == "SOP-FS-014" ? "2" : lab.Versions[id], id == "SOP-FS-014" ? oldChunks : lab.ChunksOf(id), options);
        var before = (graph.Nodes.Count, graph.Edges.Count);
        var mentionsOf2 = graph.Nodes.Sum(n => n.Mentions.Count(m => m.DocumentId == "SOP-FS-014")) + graph.Edges.Sum(e => e.Mentions.Count(m => m.DocumentId == "SOP-FS-014"));

        var clock = Stopwatch.StartNew();
        var removed = graph.RemoveVersion("SOP-FS-014", "2");
        await builder.AddAsync(graph, lab.Sources["SOP-FS-014"], "3", lab.ChunksOf("SOP-FS-014"), options);
        var incremental = clock.Elapsed.TotalMilliseconds;
        var mentionsOf3 = graph.Nodes.Sum(n => n.Mentions.Count(m => m.DocumentId == "SOP-FS-014")) + graph.Edges.Sum(e => e.Mentions.Count(m => m.DocumentId == "SOP-FS-014"));

        clock.Restart();
        var rebuilt = new KnowledgeGraph();
        foreach (var (id, source) in lab.Sources) await builder.AddAsync(rebuilt, source, lab.Versions[id], lab.ChunksOf(id), options);
        var full = clock.Elapsed.TotalMilliseconds;
        var same = graph.Nodes.Count == rebuilt.Nodes.Count && graph.Edges.Count == rebuilt.Edges.Count
            && graph.Edges.All(e => rebuilt.Edges.Any(r => r.Key == e.Key && r.Mentions.Count == e.Mentions.Count));
        Console.WriteLine($"SOP-FS-014 version 2 in the graph: {mentionsOf2} mentions; graph {before.Item1} nodes, {before.Item2} edges");
        Console.WriteLine($"Update to version 3: removed {removed.Mentions} mentions, {removed.Edges} edges, {removed.Nodes} nodes; added {mentionsOf3} mentions; {incremental:F1} ms");
        Console.WriteLine($"Full rebuild: {full:F1} ms; the updated graph equals the rebuilt one: {(same ? "yes" : "no")}");
    }
}
