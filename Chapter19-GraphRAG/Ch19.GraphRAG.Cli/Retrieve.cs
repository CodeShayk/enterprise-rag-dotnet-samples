using System.Diagnostics;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Graph;
using Contoso.Knowledge.Retrieval;
using DuckDB.NET.Data;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ch19.GraphRAG.Cli;

/// <summary>Storing the graph, walking it, and answering multi-hop questions with it.</summary>
public static class Retrieve
{
    /// <summary>
    /// The same two-hop neighborhood of ECN-2291 from three stores: the graph in memory, DuckDB in-process and
    /// PostgreSQL 17 in a container, with load time and the median of 200 queries; then a cascading delete in each.
    /// </summary>
    public static async Task StoreAsync(Lab lab)
    {
        var graph = await Extract.LoadAsync(lab, model: false);
        const string seed = "changenotice:ECN-2291";
        var search = new GraphSearch(graph, Lab.Recognizer);
        var memory = Median(200, () => search.Neighborhood([seed], 2));
        var expected = search.Neighborhood([seed], 2).SelectMany(e => new[] { e.Key.From, e.Key.To }).Append(seed).Distinct().Order().ToList();
        Console.WriteLine($"Graph: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges; two hops from {seed}: {expected.Count} nodes\n");
        Console.WriteLine($"{"Store",-28} {"load ms",8} {"2-hop ms",9} {"same nodes",11}");
        Console.WriteLine($"{"in memory (C#)",-28} {"",8} {memory,9:F3} {"yes",11}");

        await using (var duck = new DuckDBConnection("Data Source=:memory:"))
        {
            await duck.OpenAsync();
            await RunStoreAsync("DuckDB 1.5 (in-process)", duck, graph, seed, expected);
        }

        await using var postgres = new PostgreSqlBuilder("postgres:17").Build();
        await postgres.StartAsync();
        await using var pg = new NpgsqlConnection(postgres.GetConnectionString());
        await pg.OpenAsync();
        await RunStoreAsync("PostgreSQL 17 (container)", pg, graph, seed, expected);

        // Cascading delete: one service report's version out of the graph, in memory and in SQL.
        var (doc, version) = ("SR-2027-0112", lab.Versions["SR-2027-0112"]);
        var before = await GraphStore.CountAsync(pg);
        await GraphStore.RemoveVersionAsync(pg, doc, version);
        var after = await GraphStore.CountAsync(pg);
        var removed = graph.RemoveVersion(doc, version);
        Console.WriteLine($"\nRemoving {doc} version {version}: in memory {removed.Mentions} mentions, {removed.Edges} edges, {removed.Nodes} nodes; " +
            $"in PostgreSQL {before.Mentions - after.Mentions} mentions, {before.Edges - after.Edges} edges, {before.Nodes - after.Nodes} nodes");
    }

    private static async Task RunStoreAsync(string name, System.Data.Common.DbConnection connection, KnowledgeGraph graph, string seed, IReadOnlyList<string> expected)
    {
        var clock = Stopwatch.StartNew();
        await GraphStore.CreateAsync(connection, graph);
        var load = clock.Elapsed.TotalMilliseconds;
        var nodes = (await GraphStore.NeighborhoodAsync(connection, seed, 2)).Select(r => r.Node).Order().ToList();
        var times = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            clock.Restart();
            await GraphStore.NeighborhoodAsync(connection, seed, 2);
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"{name,-28} {load,8:F0} {times.Order().ElementAt(100),9:F3} {(nodes.SequenceEqual(expected) ? "yes" : "no"),11}");
    }

    private static double Median(int runs, Action action)
    {
        var times = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            var clock = Stopwatch.StartNew();
            action();
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        return times.Order().ElementAt(runs / 2);
    }

    /// <summary>Grace's question through the graph: linked entities, paths, the PageRank leaders and the evidence, for each asker's view.</summary>
    public static async Task PathsAsync(Lab lab, string id)
    {
        var graph = await Extract.LoadAsync(lab, model: false);
        var q = Lab.Questions.Single(x => x.Id == id);
        Console.WriteLine($"{q.Question}\n");
        foreach (var asker in new[] { People.Find(q.Asker)!, People.Nadia, People.Kwame }.DistinctBy(p => p.Id))
        {
            var readable = lab.Chunks.Where(c => asker.CanRead(c.Document)).Select(c => c.Document.Id).ToHashSet();
            var view = graph.View(m => readable.Contains(m.DocumentId));
            var search = new GraphSearch(view, Lab.Recognizer);
            var seeds = search.Link(q.Question);
            var rank = search.PersonalizedPageRank(seeds);
            Console.WriteLine($"As {asker.Name} ({view.Nodes.Count} nodes, {view.Edges.Count} edges visible): linked {string.Join(", ", seeds)}");
            var retriever = new GraphRetriever(graph, Lab.Recognizer, lab.Chunks);
            var hits = (await retriever.RetrieveAsync(new RetrievalRequest(q.Question, asker, 4))).Hits;
            Console.WriteLine($"  found in the passages that mention them: {string.Join(", ", retriever.LastDiscovered.Take(8))}");
            Console.WriteLine("  top edges:\n    " + search.Serialize(retriever.LastEdges.Take(6)).Replace("\n", "\n    ", StringComparison.Ordinal));
            Console.WriteLine($"  personalized PageRank: {string.Join(", ", rank.Where(r => !seeds.Contains(r.Key)).OrderByDescending(r => r.Value).Take(4).Select(r => $"{view.Find(r.Key)!.Name} {r.Value:F3}"))}");
            Console.WriteLine($"  evidence: {string.Join(", ", hits.Select(h => $"{h.Chunk.Document.Id} [{h.Chunk.Chunk.ReturnStart}-{h.Chunk.Chunk.ReturnEnd}]"))}\n");
        }
    }

    /// <summary>
    /// The multi-hop set: for each configuration, whether the re-ranked top four hold every fact, and whether Chapter
    /// 14's generator, given that evidence, states every fact.
    /// </summary>
    public static async Task MultiHopAsync(Lab lab, string split, bool answers)
    {
        var graph = await Extract.LoadAsync(lab, model: false);
        var questions = Lab.Questions.Where(q => q.Split == split).ToList();
        var chat = Lab.AnswerChat();
        Console.WriteLine($"Multi-hop questions v1, {split}: {questions.Count}\n");
        Console.WriteLine($"{"Retriever",-26} {"evidence holds",15} {"answers right",14} {"p95 s",7}");
        var rows = new Dictionary<string, List<(bool Evidence, bool Answer)>>();
        foreach (var configuration in new[] { "hybrid", "graph, one round", "graph, re-ranked", "graph", "hybrid+graph" }.Where(c => !answers || c is "hybrid" or "graph, re-ranked" or "hybrid+graph"))
        {
            var retriever = await lab.RetrieverAsync(configuration, graph);
            var generator = await lab.GeneratorAsync(retriever, chat);
            var (results, seconds) = (new List<(bool, bool)>(), new List<double>());
            foreach (var q in questions)
            {
                var asker = People.Find(q.Asker)!;
                var hits = (await retriever.RetrieveAsync(new RetrievalRequest(q.Question, asker, 4))).Hits;
                var evidence = string.Join("\n", hits.Select(h => lab.Sources[h.Chunk.Document.Id].Text[h.Chunk.Chunk.ReturnStart..h.Chunk.Chunk.ReturnEnd]));
                var holds = q.Facts.All(f => Regex.IsMatch(evidence, f.Evidence, RegexOptions.IgnoreCase));
                var right = false;
                if (answers)
                {
                    var answer = await generator.AnswerAsync(q.Question, asker);
                    seconds.Add(answer.Timings.TotalMs / 1000);
                    right = q.Facts.All(f => Regex.IsMatch(answer.Text, f.Answer, RegexOptions.IgnoreCase));
                    if (Environment.GetEnvironmentVariable("CH19_DEBUG") == "1") Console.WriteLine($"    {configuration} {q.Id} {(holds ? "holds" : "-----")} {(right ? "right" : "wrong")}: {answer.Text.ReplaceLineEndings(" ")[..Math.Min(160, answer.Text.Length)]}");
                }

                results.Add((holds, right));
            }

            rows[configuration] = results;
            var p95 = seconds.Count == 0 ? "" : seconds.Order().ElementAt((int)Math.Ceiling(0.95 * seconds.Count) - 1).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"{configuration,-26} {results.Count(r => r.Item1) + "/" + results.Count,15} {(answers ? results.Count(r => r.Item2) + "/" + results.Count : ""),14} {p95,7}");
        }

        Console.WriteLine($"\nBy question, evidence held ({string.Join(" / ", rows.Keys)}):");
        Console.WriteLine("  " + string.Join("  ", questions.Select((q, i) => $"{q.Id} {string.Concat(rows.Values.Select(r => Mark(r[i].Evidence)))}")));
        static string Mark(bool b) => b ? "+" : "-";
    }
}
