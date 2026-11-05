using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Operations;
using Contoso.Knowledge.Retrieval;
using static System.FormattableString;

namespace Ch24.Deployment.Cli;

/// <summary>The embedding model down or slow: the hybrid retriever answers from the keyword index alone and says so.</summary>
public static class DegradedCommand
{
    public static async Task RunAsync(Lab lab)
    {
        var catalog = new IndexCatalog(Path.Combine(Lab.Runs, "releases"));
        var live = catalog.Alias("live")?.Version ?? throw new InvalidOperationException("Run release first.");
        var dev = lab.Split("dev");
        Console.WriteLine($"Live index {live}, {dev.Count} dev questions (sufficiency over the {dev.Count(q => q.Answerable)} answerable), hybrid with a 2 s limit per source, re-ranked, top 4");
        Console.WriteLine($"{"Embedding model",-30} {"sufficient",10} {"p50 ms",7} {"p95 ms",7}  degraded");
        foreach (var (name, wrap) in new (string, Func<IRetriever, IRetriever>?)[]
        {
            ("answering", null),
            ("down (throws)", dense => new FaultyRetriever(dense, TimeSpan.Zero, fail: true)),
            ("slow (3 s a query)", dense => new FaultyRetriever(dense, TimeSpan.FromSeconds(3), fail: false)),
        })
        {
            var served = await lab.LoadAsync(catalog, live, wrapDense: wrap);
            var questions = name.StartsWith("slow", StringComparison.Ordinal) ? dev.Take(20).ToList() : dev;
            var (times, reasons) = (new List<double>(), new HashSet<string>());
            foreach (var q in questions)
            {
                var clock = Stopwatch.StartNew();
                var result = await served.Retriever.RetrieveAsync(new RetrievalRequest(q.Question, People.Find(q.Asker)!, 4));
                times.Add(clock.Elapsed.TotalMilliseconds);
                reasons.UnionWith(result.Degraded);
            }

            var scores = await lab.ScoreAsync(served, questions);
            times.Sort();
            Console.WriteLine(Invariant($"{name,-30} {scores.Values.Average(),10:F2} {times[times.Count / 2],7:F0} {times[(int)(times.Count * 0.95)],7:F0}  {(reasons.Count == 0 ? "no" : string.Join("; ", reasons))}") +
                (questions.Count < dev.Count ? $" (first {questions.Count} questions)" : ""));
        }
    }
}

/// <summary>A retriever that fails, or answers late, the way an embedding service does when it's down or overloaded.</summary>
public sealed class FaultyRetriever(IRetriever inner, TimeSpan delay, bool fail) : IRetriever
{
    public string Name => inner.Name;

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        if (fail) throw new HttpRequestException("embedding service unavailable");
        await Task.Delay(delay, cancellationToken);
        return await inner.RetrieveAsync(request, cancellationToken);
    }
}
