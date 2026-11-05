using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ch25.Knowledge.Api;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Operations;
using Contoso.Knowledge.Retrieval;
using static System.FormattableString;

namespace Ch25.Readiness.Cli;

/// <summary>
/// Leave one out: the shipped retrieval with one technique taken away at a time, each measured on golden set v2's dev
/// and test questions against the shipped configuration, question by question, with what it costs to build and search.
/// </summary>
public static class Ablation
{
    public static IReadOnlyList<(string Name, string Chapter, IndexSettings Index, RetrievalSettings Retrieval)> Variants { get; } =
    [
        ("shipped", "", IndexSettings.Shipped, RetrievalSettings.Shipped),
        ("- ingestion hygiene", "6", IndexSettings.Shipped with { Name = "no-hygiene", Hygiene = false }, RetrievalSettings.Shipped),
        ("- parent-child chunks", "9", IndexSettings.Shipped with { Name = "fixed-200", ParentChild = false }, RetrievalSettings.Shipped),
        ("- breadcrumbs", "9", IndexSettings.Shipped with { Name = "no-breadcrumbs", Breadcrumbs = false }, RetrievalSettings.Shipped),
        ("- INT8 (FP32 model)", "10", IndexSettings.Shipped with { Name = "fp32", Int8 = false }, RetrievalSettings.Shipped),
        ("- dense search", "10", IndexSettings.Shipped, RetrievalSettings.Shipped with { Dense = false }),
        ("- keyword search", "11", IndexSettings.Shipped, RetrievalSettings.Shipped with { Keyword = false }),
        ("- identifier analyzer", "11", IndexSettings.Shipped, RetrievalSettings.Shipped with { Identifiers = false }),
        ("- re-ranking", "12", IndexSettings.Shipped, RetrievalSettings.Shipped with { Rerank = false }),
        ("- graph route", "19", IndexSettings.Shipped, RetrievalSettings.Shipped with { Graph = false }),
        ("- permissions in the search", "22", IndexSettings.Shipped, RetrievalSettings.Shipped with { TrimInSearch = false }),
        ("- work-order export", "17, 22", IndexSettings.Shipped with { Name = "no-work-orders", WorkOrders = false }, RetrievalSettings.Shipped),
    ];

    public static async Task RunAsync(string runs)
    {
        var corpus = await ContosoIndex.LoadAsync();
        var golden = GoldenSet.Load(ContosoIndex.GoldenSetPath);
        var (dev, test) = (golden.Where(q => q.Split == "dev").ToList(), golden.Where(q => q.Split == "test").ToList());
        var catalog = new IndexCatalog(Path.Combine(runs, "ablation"));
        var built = new Dictionary<string, (IndexManifest Manifest, double Seconds)>();
        Dictionary<string, double>? shippedDev = null, shippedTest = null;
        var rows = new List<object>();

        Console.WriteLine($"Golden set v2 ({GoldenSet.Sha256(ContosoIndex.GoldenSetPath)[..12]}), sufficient top 4 on {dev.Count(q => q.Answerable)} answerable dev and {test.Count(q => q.Answerable)} test questions; i7-12700H, CPU only");
        Console.WriteLine($"{"Configuration",-28} {"Ch",-3} {"chunks",6} {"build s",7} {"p50 ms",6} {"dev",5} {"change on dev",22} {"test",5} {"change on test",22}");
        foreach (var (name, chapter, index, retrieval) in Variants)
        {
            if (!built.TryGetValue(index.Name, out var build))
            {
                var clock = Stopwatch.StartNew();
                var manifest = await corpus.BuildAsync(catalog, index.Name, index);
                built[index.Name] = build = (manifest, clock.Elapsed.TotalSeconds);
            }

            var served = await corpus.LoadAsync(catalog, index.Name, retrieval);
            var d = new Dictionary<string, double>(await corpus.ScoreAsync(served.Retriever, served.Chunks, dev));
            var t = new Dictionary<string, double>(await corpus.ScoreAsync(served.Retriever, served.Chunks, test));
            (shippedDev, shippedTest) = (shippedDev ?? d, shippedTest ?? t);

            // Search time, once the models are warm: every dev question as its own asker.
            var times = new List<double>();
            foreach (var q in dev)
            {
                var clock = Stopwatch.StartNew();
                await served.Retriever.RetrieveAsync(new RetrievalRequest(q.Question, People.Find(q.Asker)!, 4));
                times.Add(clock.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            var (cd, ct) = (Change(shippedDev, d), Change(shippedTest, t));
            Console.WriteLine(Invariant($"{name,-28} {chapter,-3} {build.Manifest.Chunks,6} {build.Seconds,7:F1} {times[times.Count / 2],6:F0} {d.Values.Average(),5:F2} {Format(cd),22} {t.Values.Average(),5:F2} {Format(ct),22}"));
            rows.Add(new
            {
                name, chapter, chunks = build.Manifest.Chunks, buildSeconds = Math.Round(build.Seconds, 1), p50Ms = Math.Round(times[times.Count / 2], 1),
                dev = Math.Round(d.Values.Average(), 3), devChange = cd, devLost = Lost(shippedDev, d), test = Math.Round(t.Values.Average(), 3), testChange = ct, testLost = Lost(shippedTest, t),
            });
        }

        File.WriteAllText(Path.Combine(runs, "ablation.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        // No golden question chains relations, so the graph route's row can't move; Chapter 19's questions show what it buys.
        var multiHop = File.ReadLines(ReviewContext.Data("multihop.v1.jsonl")).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var taken = multiHop.Count(q => GraphRoute.IsMultiHop(q.GetProperty("question").GetString()!));
        Console.WriteLine($"\nChapter 19's {multiHop.Count} multi-hop questions, {taken} of them on the graph route; the top 4 hold every fact:");
        foreach (var (name, settings) in new[] { ("with the graph route", RetrievalSettings.Shipped), ("without it", RetrievalSettings.Shipped with { Graph = false }) })
        {
            var served = await corpus.LoadAsync(catalog, IndexSettings.Shipped.Name, settings);
            var held = 0;
            foreach (var q in multiHop)
            {
                var result = await served.Retriever.RetrieveAsync(new RetrievalRequest(q.GetProperty("question").GetString()!, People.Find(q.GetProperty("asker").GetString()!)!, 4));
                var evidence = string.Join("\n", result.Hits.Select(h => h.Chunk.Document.Text[h.Chunk.Chunk.ReturnStart..h.Chunk.Chunk.ReturnEnd]));
                if (q.GetProperty("facts").EnumerateArray().All(f => Regex.IsMatch(evidence, f.GetProperty("evidence").GetString()!, RegexOptions.IgnoreCase))) held++;
            }

            Console.WriteLine($"  {name,-22} {held} of {multiHop.Count}");
        }
    }

    /// <summary>The paired difference against the shipped configuration, with Chapter 5's bootstrap interval.</summary>
    private static Difference Change(IReadOnlyDictionary<string, double> shipped, IReadOnlyDictionary<string, double> variant)
    {
        var ids = shipped.Keys.Order(StringComparer.Ordinal).ToList();
        return Bootstrap.Paired([.. ids.Select(id => shipped[id])], [.. ids.Select(id => variant[id])]);
    }

    private static IReadOnlyList<string> Lost(IReadOnlyDictionary<string, double> shipped, IReadOnlyDictionary<string, double> variant) =>
        [.. shipped.Where(p => variant[p.Key] < p.Value).Select(p => p.Key).Order(StringComparer.Ordinal)];

    private static string Format(Difference d) => d.Mean == 0 && d.Low == 0 && d.High == 0 ? "+0.00"
        : $"{Signed(d.Mean)} [{Signed(d.Low)}, {Signed(d.High)}]";

    private static string Signed(double x) => (Math.Round(x, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture);
}
