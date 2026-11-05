using System.Diagnostics;
using System.Text.Json;
using Contoso.Knowledge.Operations;
using static System.FormattableString;

namespace Ch24.Deployment.Cli;

/// <summary>
/// The release stage of the pipeline: build the candidate index beside the live one, gate it on the dev questions,
/// swap the live alias, check the held-out test questions through the alias as the service now reads it, and roll back
/// on its own when they regress. Scores are kept with each version, so the live version is never evaluated twice.
/// </summary>
public static class ReleaseCommand
{
    public const string QueryEncoder = "bge-small-en-v1.5-int8";

    private static readonly GatePolicy Policy = new(0.03, new HashSet<string>(["G001", "G003", "G004", "G005", "G006", "G017"]));

    public static async Task RunAsync(Lab lab, string candidate)
    {
        var catalog = new IndexCatalog(Path.Combine(Lab.Runs, "releases"));
        var clock = Stopwatch.StartNew();
        if (catalog.Alias("live") is null)
        {
            await lab.BuildAsync(catalog, "v1-shipped", Lab.Shipped);
            catalog.Swap("live", "v1-shipped", QueryEncoder, Lab.Today);
            Console.WriteLine(Invariant($"initial deployment: live -> v1-shipped ({clock.Elapsed.TotalSeconds:F1} s)"));
        }

        var live = catalog.Alias("live")!.Version;
        var config = Lab.Candidates.Single(c => c.Name == candidate);
        var version = $"v{Directory.GetDirectories(Path.Combine(catalog.Root, "versions")).Length + 1}-{candidate}";
        Console.WriteLine($"release {version} against live {live}; gate: lower bound above -{Policy.WorstDrop:0.00}, no must-pass question lost");

        clock.Restart();
        var manifest = await lab.BuildAsync(catalog, version, config);
        Console.WriteLine(Invariant($"  build   {manifest.Chunks} chunks, {manifest.Chunker}, breadcrumbs {(manifest.Breadcrumbs ? "on" : "off")}, {manifest.EmbeddingModel} ({clock.Elapsed.TotalSeconds:F1} s)"));

        clock.Restart();
        var gate = await GateAsync(lab, catalog, live, version, "dev");
        Console.WriteLine(Invariant($"  gate    dev, {gate.Count} answerable questions: {Describe(gate.Result)} ({clock.Elapsed.TotalSeconds:F1} s)"));
        if (!gate.Result.Passed)
        {
            Console.WriteLine($"  stopped: {version} is not deployed; live stays {live}");
            Environment.ExitCode = 1;
            return;
        }

        clock.Restart();
        var swapped = catalog.Swap("live", version, QueryEncoder, Lab.Today);
        Console.WriteLine(Invariant($"  swap    live: {swapped.Previous} -> {swapped.Version} ({clock.Elapsed.TotalMilliseconds:F1} ms)"));

        // The service reads the alias; the check goes through it, with the service's own query encoder.
        clock.Restart();
        var check = await GateAsync(lab, catalog, swapped.Previous!, catalog.Alias("live")!.Version, "test");
        Console.WriteLine(Invariant($"  check   test, {check.Count} answerable questions through the live alias: {Describe(check.Result)} ({clock.Elapsed.TotalSeconds:F1} s)"));
        if (!check.Result.Passed)
        {
            clock.Restart();
            var back = catalog.Rollback("live", QueryEncoder, Lab.Today);
            Console.WriteLine(Invariant($"  rollback live: {back.Previous} -> {back.Version} ({clock.Elapsed.TotalMilliseconds:F1} ms); {version} kept for diagnosis"));
            Environment.ExitCode = 1;
        }
    }

    private static async Task<(int Count, GateResult Result)> GateAsync(Lab lab, IndexCatalog catalog, string live, string candidate, string split)
    {
        var questions = lab.Split(split);
        var baseline = await ScoresAsync(lab, catalog, live, split);
        var scores = await ScoresAsync(lab, catalog, candidate, split);
        return (baseline.Count, ReleaseGate.Evaluate([.. baseline.Select(b => (b.Key, b.Value, scores[b.Key]))], Policy));
    }

    /// <summary>A version's per-question sufficiency on a split, computed once and kept beside the version.</summary>
    private static async Task<Dictionary<string, double>> ScoresAsync(Lab lab, IndexCatalog catalog, string version, string split)
    {
        var path = Path.Combine(catalog.PathOf(version), $"scores-{split}.json");
        if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, double>>(await File.ReadAllTextAsync(path))!;
        var scores = new Dictionary<string, double>(await lab.ScoreAsync(await lab.LoadAsync(catalog, version), lab.Split(split)));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(scores));
        return scores;
    }

    private static string Describe(GateResult r) =>
        Invariant($"change {r.Change.Mean:+0.000;-0.000} [{r.Change.Low:+0.000;-0.000}, {r.Change.High:+0.000;-0.000}], ") +
        $"worse {(r.Regressed.Count == 0 ? "none" : string.Join(" ", r.Regressed))}, better {(r.Improved.Count == 0 ? "none" : string.Join(" ", r.Improved))}: {r.Reason}";
}
