using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Contoso.Knowledge.Performance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static System.FormattableString;

namespace Ch21.Performance.Cli;

/// <summary>Cost per answer and per tenant from measured runs, and the limits that keep a tenant within its share.</summary>
public static class Operations
{
    // Quality scores are NaN for unanswerable questions; JSON numbers can't say NaN without the named literal.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static string Money(decimal value) => "$" + value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The chapter's result: Section 21.4's simulated day served by the 3B model or by the cascade, with or without the
    /// answer cache. Each request takes its question's measured time, CPU and tokens from the cascade run; a cache hit
    /// still pays for retrieval.
    /// </summary>
    public static void Result(Lab lab, string split = "dev")
    {
        var prices = new Prices();
        var rows = File.ReadLines(Path.Combine(Lab.Runs, $"cascade-{split}.jsonl")).Select(l => JsonSerializer.Deserialize<RunRow>(l, Json)!)
            .ToDictionary(r => (r.Id, r.Config));
        var day = Caches.Day([.. lab.Split(split)]).ToList();
        Console.WriteLine($"Section 21.4's simulated day: {day.Count} requests over the {split} questions, times as measured or recorded");
        Console.WriteLine($"{"Configuration",-28} {"p50 s",6} {"p95 s",6} {"mean s",7} {"model calls",12} {"own CPU",9} {"API",8}");
        foreach (var (config, cached) in new[] { ("3B", false), ("cascade", false), ("3B", true), ("cascade", true) })
        {
            var (latencies, local, api, calls) = Serve(day, rows, config, cached, prices);
            var name = config == "3B" ? "llama3.2 3B" : "cascade, 1B then 3B";
            Console.WriteLine(Invariant($"{(cached ? name + ", cached" : name),-28} {Stats.Percentile(latencies, 0.5),6:F1} {Stats.Percentile(latencies, 0.95),6:F1} {latencies.Average(),7:F1} {calls,12} {Money(local / day.Count * 1000),8} {Money(api / day.Count * 1000),7}"));
        }

        Console.WriteLine("(own CPU and API: cost per 1,000 requests at Chapter 4's example prices)");
    }

    /// <summary>One simulated day served by a configuration: each request's latency, the day's costs and its model calls.</summary>
    private static (List<double> Latencies, decimal Local, decimal Api, int Calls) Serve(
        IReadOnlyList<Contoso.Knowledge.Evaluation.GoldenQuestion> day, IReadOnlyDictionary<(string, string), RunRow> rows, string config, bool cached, Prices prices)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var (latencies, local, api, calls) = (new List<double>(), 0m, 0m, 0);
        foreach (var q in day)
        {
            var r = rows[(q.Id, config)];
            var hit = cached && r.Model && !seen.Add(q.Id);
            if (r.Model && !hit) calls += r.Escalated ? 2 : 1;
            latencies.Add(hit ? r.EvidenceMs / 1000 : r.TotalMs / 1000);
            var usage = hit ? new AnswerUsage(r.AppCpu, 0, 0, 0, 0) : new AnswerUsage(r.AppCpu, r.ModelCpu, r.PromptTokens, r.OutputTokens, 0);
            (local, api) = (local + CostModel.Local(usage, prices), api + CostModel.Api(usage, prices));
        }

        return (latencies, local, api, calls);
    }

    /// <summary>Cost per answer under Chapter 4's example prices, from the cascade run's measured CPU and tokens.</summary>
    public static void Cost(Lab lab, string split = "dev")
    {
        var prices = new Prices();
        var path = Path.Combine(Lab.Runs, $"cascade-{split}.jsonl");
        if (!File.Exists(path)) throw new InvalidOperationException($"Run the cascade first: {path} is missing.");
        var rows = File.ReadLines(path).Select(l => JsonSerializer.Deserialize<RunRow>(l, Json)!).ToList();
        Console.WriteLine($"Cost per 1,000 answers, {split} ({rows.Count / 3} questions), at ${prices.PerCoreHour}/core-hour and ${prices.InputPerMillion}/${prices.OutputPerMillion} per million tokens");
        Console.WriteLine($"{"Configuration",-10} {"app CPU-s",10} {"model CPU-s",12} {"tokens in/out",14} {"own CPU",9} {"API",8}");
        foreach (var config in new[] { "3B", "1B", "cascade" })
        {
            var r = rows.Where(x => x.Config == config).ToList();
            var usage = r.Select(x => new AnswerUsage(x.AppCpu, x.ModelCpu, x.PromptTokens, x.OutputTokens, x.TotalMs / 1000)).ToList();
            Console.WriteLine(Invariant($"{config,-10} {usage.Average(u => u.AppCpuSeconds),10:F2} {usage.Average(u => u.ModelCpuSeconds),12:F1} {$"{usage.Average(u => u.InputTokens):F0}/{usage.Average(u => u.OutputTokens):F0}",14} {Money(usage.Sum(u => CostModel.Local(u, prices)) / r.Count * 1000),8} {Money(usage.Sum(u => CostModel.Api(u, prices)) / r.Count * 1000),7}"));
        }

        var tenants = rows.Where(x => x.Config == "cascade").GroupBy(x => Contoso.Corpus.People.Find(lab.Questions.Single(q => q.Id == x.Id).Asker)!.Tenant);
        Console.WriteLine("cascade per tenant, own CPU: " + string.Join("; ", tenants.Select(g =>
            Invariant($"{g.Key} {g.Count()} answers, {Money(g.Sum(x => CostModel.Local(new AnswerUsage(x.AppCpu, x.ModelCpu, 0, 0, 0), prices)) / g.Count() * 1000)} per 1,000"))));

        var load = Path.Combine(Lab.Runs, "load.jsonl");
        if (File.Exists(load))
        {
            var levels = File.ReadLines(load).Select(l => JsonSerializer.Deserialize<LoadLevel>(l, Json)!).ToList();
            var best = levels.MaxBy(l => l.PerHour)!;
            var node = CostModel.PerAnswerOnNode(Environment.ProcessorCount, best.PerHour, prices);
            Console.WriteLine(Invariant($"a {Environment.ProcessorCount}-thread node at its best load-test rate ({best.PerHour:F0} answers an hour, {best.Workers} workers): {Money(node * 1000)} per 1,000 answers when busy"));
            Console.WriteLine(Invariant($"Chapter 4's peak hour (900 answers): {Math.Ceiling(900 / best.PerHour):F0} such nodes"));

            // A day at Chapter 4's demand: 6,000 answers, 900 in the peak hour. Nodes are sized for the peak hour's model
            // calls and paid around the clock; the API is paid per request, like Output 21.11's simulated day scaled up.
            var byKey = rows.ToDictionary(r => (r.Id, r.Config));
            var day = Caches.Day([.. lab.Split(split)]).ToList();
            Console.WriteLine("A day at Chapter 4's demand (6,000 answers, 900 in the peak hour), llama3.2 3B:");
            foreach (var cached in new[] { false, true })
            {
                var (_, _, api, calls) = Serve(day, byKey, "3B", cached, prices);
                var nodes = Math.Max(1, Math.Ceiling(900.0 * calls / day.Count / best.PerHour));
                var own = (decimal)nodes * Environment.ProcessorCount * prices.PerCoreHour * 24;
                Console.WriteLine(Invariant($"  {(cached ? "with the answer cache" : "without a cache"),-22} model calls {calls / (double)day.Count:P0}: own nodes {nodes} x 24 h {Money(own)}; metered API {Money(api / day.Count * 6000)}"));
            }
        }
    }

    /// <summary>
    /// Limits on a minimal API in memory: a token bucket per tenant, a daily token quota and a gate that sheds model
    /// calls when its queue is full.
    /// </summary>
    public static async Task LimitsAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // A header stands in for the tenant claim of the validated identity (Chapter 22): partitioning on input
            // the caller controls lets a caller make partitions without limit.
            options.AddPolicy("per-tenant", http => RateLimitPartition.GetTokenBucketLimiter(
                partitionKey: http.Request.Headers["X-Tenant"].ToString(),
                factory: _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 10,
                    TokensPerPeriod = 10,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapPost("/ask", () => Results.Ok()).RequireRateLimiting("per-tenant");
        await app.StartAsync();
        using var client = app.GetTestClient();
        async Task<string> Send(string tenant, int count)
        {
            var codes = new List<HttpStatusCode>();
            for (var i = 0; i < count; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/ask");
                request.Headers.Add("X-Tenant", tenant);
                codes.Add((await client.SendAsync(request)).StatusCode);
            }

            return $"{codes.Count(c => c == HttpStatusCode.OK)} answered, {codes.Count(c => c == HttpStatusCode.TooManyRequests)} refused (429)";
        }

        Console.WriteLine("Token bucket per tenant (10 requests, 10 more a minute), sent within a second:");
        Console.WriteLine($"  fluid sends 15: {await Send("fluid", 15)}");
        Console.WriteLine($"  service sends 5: {await Send("service", 5)}");

        var budget = new TenantTokenBudget(new Dictionary<string, long> { ["fluid"] = 5_000 });
        var served = 0;
        while (budget.TryReserve("fluid", 1_300))
        {
            budget.Settle("fluid", 1_300, 1_243);
            served++;
        }

        Console.WriteLine($"Daily quota of 5,000 model tokens for fluid, 1,300 reserved an answer (1,243 used): {served} answers, then refused at {budget.Used("fluid"):N0} tokens");

        using var gate = new ModelGate(concurrent: 1, queue: 2);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            using var lease = await gate.EnterAsync();
            if (!lease.IsAcquired) return "degraded";
            await Task.Delay(200);
            return "answered";
        }));
        Console.WriteLine($"Model gate (1 call at a time, a queue of 2), 5 requests at once: {outcomes.Count(o => o == "answered")} answered, {outcomes.Count(o => o == "degraded")} degraded to sources only");
    }
}

/// <summary>One concurrency level of the load test, as written to <c>load.jsonl</c>.</summary>
public sealed record LoadLevel(int Workers, int Answers, double Seconds, double PerHour, double P50, double P95);
