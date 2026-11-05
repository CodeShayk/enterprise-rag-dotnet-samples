using Contoso.Knowledge.Operations;
using static System.FormattableString;

namespace Ch24.Deployment.Cli;

/// <summary>Candidate index configurations against the shipped one, on the dev and the test questions.</summary>
public static class CandidatesCommand
{
    public static async Task RunAsync(Lab lab)
    {
        var catalog = new IndexCatalog(Path.Combine(Lab.Runs, "candidates"));
        var (dev, test) = (lab.Split("dev"), lab.Split("test"));
        Dictionary<string, double>? shippedDev = null, shippedTest = null;
        Console.WriteLine($"Hybrid, re-ranked, top 4; sufficiency on the answerable dev ({dev.Count(q => q.Answerable)}) and test ({test.Count(q => q.Answerable)}) questions; changes against the shipped index");
        Console.WriteLine($"{"Index",-15} {"chunks",6} {"dev",5} {"test",5}  {"dev: worse/better",17}  {"test: worse/better",18}");
        foreach (var config in Lab.Candidates)
        {
            var manifest = await lab.BuildAsync(catalog, config.Name, config);
            var served = await lab.LoadAsync(catalog, config.Name, queryInt8: config.Int8);
            var (d, t) = (new Dictionary<string, double>(await lab.ScoreAsync(served, dev)), new Dictionary<string, double>(await lab.ScoreAsync(served, test)));
            (shippedDev, shippedTest) = (shippedDev ?? d, shippedTest ?? t);
            string Changes(Dictionary<string, double> live, Dictionary<string, double> candidate) =>
                $"{live.Count(p => candidate[p.Key] < p.Value)}/{live.Count(p => candidate[p.Key] > p.Value)}";
            Console.WriteLine(Invariant($"{config.Name,-15} {manifest.Chunks,6} {d.Values.Average(),5:F2} {t.Values.Average(),5:F2}  {Changes(shippedDev, d),17}  {Changes(shippedTest, t),18}"));
        }
    }
}
