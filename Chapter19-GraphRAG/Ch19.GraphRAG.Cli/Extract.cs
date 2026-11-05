using System.Diagnostics;
using Contoso.Knowledge.Graph;

namespace Ch19.GraphRAG.Cli;

/// <summary>Building the graph, and measuring its extraction against the labeled edges.</summary>
public static class Extract
{
    public static string GraphFile(bool model) => Path.Combine(Lab.Runs, model ? "graph.json" : "graph-rules.json");

    /// <summary>The graph from every document: rules always, the schema-bound model when asked; saved for the other commands.</summary>
    public static async Task<KnowledgeGraph> BuildAsync(Lab lab, bool model, bool print = true)
    {
        var graph = new KnowledgeGraph();
        var builder = new GraphBuilder(Lab.Recognizer);
        var extractor = model ? new ModelRelationExtractor(Lab.GraphChat(), maxCalls: 400) : null;
        var options = new ExtractionOptions(Model: extractor, Selective: true);
        var clock = Stopwatch.StartNew();
        foreach (var (id, source) in lab.Sources) await builder.AddAsync(graph, source, lab.Versions[id], lab.ChunksOf(id), options);
        Directory.CreateDirectory(Lab.Runs);
        graph.Save(GraphFile(model));
        if (!print) return graph;

        Console.WriteLine($"{lab.Sources.Count} documents, {lab.Chunks.Count} chunks: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges, {graph.Nodes.Sum(n => n.Mentions.Count) + graph.Edges.Sum(e => e.Mentions.Count)} mentions in {clock.Elapsed.TotalMilliseconds:F0} ms");
        if (extractor is not null) Console.WriteLine($"Model calls: {extractor.Calls} chunks, {extractor.Time.TotalSeconds:F0} s of model time (replayed calls take none)");
        Console.WriteLine("Nodes: " + string.Join(", ", graph.Nodes.GroupBy(n => n.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine("Edges: " + string.Join(", ", graph.Edges.GroupBy(e => e.Key.Relation).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine("Edges by method: " + string.Join(", ", graph.Edges.SelectMany(e => e.Mentions.Select(m => m.Method).Distinct()).GroupBy(m => m).Select(g => $"{g.Key} {g.Count()}")));
        return graph;
    }

    public static async Task<KnowledgeGraph> LoadAsync(Lab lab, bool model) =>
        File.Exists(GraphFile(model)) ? KnowledgeGraph.Load(GraphFile(model)) : await BuildAsync(lab, model, print: false);

    /// <summary>
    /// Extraction precision and recall on the 15 labeled documents: table rows alone, then document-subject rules,
    /// the schema-bound model and both, each against the 69 labeled edges.
    /// </summary>
    public static async Task EvaluateAsync(Lab lab, bool model = true)
    {
        var docs = Lab.Truth.Select(t => t.Doc).ToHashSet();
        var truth = Lab.Truth.Select(t => (t.Doc, t.From, t.Relation, t.To)).ToHashSet();
        Console.WriteLine($"{docs.Count} labeled documents, {truth.Count} labeled edges\n");
        Console.WriteLine($"{"Extraction",-30} {"edges",6} {"right",6} {"precision",10} {"recall",7} {"F1",6} {"calls",6} {"model s",8}");
        ModelRelationExtractor? last = null;
        foreach (var (name, options) in new (string, Func<ModelRelationExtractor, ExtractionOptions>)[]
        {
            ("tables", _ => new ExtractionOptions(Subject: false)),
            ("tables + subject rules", _ => new ExtractionOptions()),
            ("tables + model", m => new ExtractionOptions(Subject: false, Model: m)),
            ("tables + subject rules + model", m => new ExtractionOptions(Model: m)),
            ("... model only without a subject", m => new ExtractionOptions(Model: m, Selective: true)),
        }.Where(c => model || !c.Item1.Contains("model", StringComparison.Ordinal)))
        {
            var extractor = last = new ModelRelationExtractor(Lab.GraphChat(), maxCalls: 400);
            var found = new HashSet<(string, string, string, string)>();
            foreach (var doc in docs)
            {
                var graph = new KnowledgeGraph();
                await new GraphBuilder(Lab.Recognizer).AddAsync(graph, lab.Sources[doc], lab.Versions[doc], lab.ChunksOf(doc), options(extractor));
                foreach (var e in graph.Edges) found.Add((doc, e.Key.From, e.Key.Relation, e.Key.To));
            }

            var right = found.Count(truth.Contains);
            var (precision, recall) = (right / (double)Math.Max(1, found.Count), right / (double)truth.Count);
            var calls = options(extractor).Model is null ? "" : extractor.Calls.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var seconds = options(extractor).Model is null ? "" : extractor.Time.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"{name,-30} {found.Count,6} {right,6} {precision,10:F2} {recall,7:F2} {2 * precision * recall / Math.Max(1e-9, precision + recall),6:F2} {calls,6} {seconds,8}");
            if (Environment.GetEnvironmentVariable("CH19_DEBUG") == "1")
            {
                foreach (var f in found.Except(truth).Take(40)) Console.WriteLine($"    extra   {f}");
                foreach (var m in truth.Except(found).Take(40)) Console.WriteLine($"    missing {m}");
            }
        }

        if (last is { Calls: > 0 })
        {
            Console.WriteLine($"\nThe last run's model proposals: {last.Proposed} kept ({last.Renamed} naming an entity by its text instead of its ID, {last.Flipped} written backwards), {last.Rejected} dropped by the schema");
        }
    }
}
