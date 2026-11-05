using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ch25.Knowledge.Api;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;

namespace Ch25.Readiness.Cli;

public enum CheckStatus
{
    Pass,
    Warn,
    Fail,
    NotRun,
}

/// <summary>What a check found: its status and the evidence behind it, in words a reviewer can verify.</summary>
public sealed record Finding(CheckStatus Status, string Evidence);

/// <summary>One item of a checklist: what it checks, which chapters built what it checks, who owns a failure, and how to run it.</summary>
public sealed record Check(string Id, string Area, string Name, string Chapters, string Owner, Func<ReviewContext, Task<Finding>> RunAsync);

public sealed record CheckResult(Check Check, Finding Finding, double Seconds);

/// <summary>
/// What the checks share: the running reference implementation, its assembled parts, golden set v2, and every answer
/// the review asked for, so the latency, provenance and telemetry checks can look at the review's own traffic.
/// </summary>
public sealed class ReviewContext(ReferenceDeployment service, AssistantParts parts, string model, string runs)
{
    public ReferenceDeployment Service => service;

    public AssistantParts Parts => parts;

    public string Model => model;

    public bool LiveModel => model != "scripted";

    public string Runs => runs;

    public IReadOnlyList<GoldenQuestion> Golden { get; } = GoldenSet.Load(ContosoIndex.GoldenSetPath);

    public GoldenQuestion Question(string id) => Golden.Single(q => q.Id == id);

    public List<AnswerReply> Answers { get; } = [];

    public List<Activity> Spans { get; init; } = [];

    /// <summary>The model client's resilience events while the review ran, from Polly's metrics.</summary>
    public ResilienceEvents Resilience { get; } = new();

    /// <summary>Per-question sufficiency from the quality checklist's retrieval run, for the checks that follow it.</summary>
    public Dictionary<string, double> Sufficiency { get; } = [];

    public static string Data(string file) => Path.Combine(AppContext.BaseDirectory, "data", file);

    public async Task<AnswerReply> AskAsync(string question, Person asker)
    {
        var (status, reply) = await service.AskAsync(question, asker);
        if (reply is null) throw new InvalidOperationException($"POST /ask returned {status}.");
        Answers.Add(reply);
        return reply;
    }

    /// <summary>Another instance of the reference implementation in a folder of its own, for checks that change its wiring.</summary>
    public ReferenceDeployment Deploy(string name, IReadOnlyDictionary<string, string?>? settings = null, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null) =>
        new(Path.Combine(runs, name), settings, services);
}

/// <summary>
/// Counts the model client's timeouts from Polly's metrics (meter <c>Polly</c>, counter
/// <c>resilience.polly.strategy.events</c>), so the latency check can say how often the first-token limit fired.
/// </summary>
public sealed class ResilienceEvents : IDisposable
{
    private readonly MeterListener _listener = new();
    private int _timeouts;

    public ResilienceEvents()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Polly" && instrument.Name == "resilience.polly.strategy.events") listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<int>((_, _, tags, _) => Count(tags));
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Count(tags));
        _listener.Start();
    }

    public int Timeouts => _timeouts;

    private void Count(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "event.name" && tag.Value as string == "OnTimeout") Interlocked.Increment(ref _timeouts);
        }
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>Runs every check, records what it found and writes the scorecard as Markdown and JSON.</summary>
public static class Review
{
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(ReviewContext context, IEnumerable<Check> checks)
    {
        var results = new List<CheckResult>();
        foreach (var check in checks)
        {
            var clock = Stopwatch.StartNew();
            Finding finding;
            try
            {
                finding = await check.RunAsync(context);
            }
            catch (Exception e)
            {
                finding = new Finding(CheckStatus.Fail, $"the check itself failed: {e.GetType().Name}: {e.Message}");
            }

            results.Add(new CheckResult(check, finding, clock.Elapsed.TotalSeconds));
            Console.WriteLine($"{Label(finding.Status),-8} {check.Id,-3} {check.Name}: {finding.Evidence}");
        }

        return results;
    }

    public static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Warn => "WARN",
        CheckStatus.Fail => "FAIL",
        _ => "NOT RUN",
    };

    /// <summary>Any failure blocks the release; warnings become its conditions; checks not run are listed as untested.</summary>
    public static string Decision(IReadOnlyList<CheckResult> results) =>
        results.Any(r => r.Finding.Status == CheckStatus.Fail) ? "not ready: every FAIL blocks the release"
        : results.Any(r => r.Finding.Status == CheckStatus.Warn) ? "ready with conditions: each WARN needs an owner and a date"
        : "ready";

    public static string Totals(IReadOnlyList<CheckResult> results) =>
        string.Join(", ", new[] { CheckStatus.Pass, CheckStatus.Warn, CheckStatus.Fail, CheckStatus.NotRun }.Select(s => $"{results.Count(r => r.Finding.Status == s)} {Label(s).ToLowerInvariant()}"));

    public static void Publish(ReviewContext context, IReadOnlyList<CheckResult> results, string folder, string header)
    {
        Directory.CreateDirectory(folder);
        var markdown = new StringBuilder()
            .AppendLine("# Readiness review: Contoso Knowledge Assistant")
            .AppendLine()
            .AppendLine(header)
            .AppendLine()
            .AppendLine($"**Decision:** {Decision(results)} ({Totals(results)}).")
            .AppendLine()
            .AppendLine("| Check | Area | Status | Evidence | Chapters | Owner |")
            .AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            markdown.AppendLine($"| {r.Check.Id} {r.Check.Name} | {r.Check.Area} | {Label(r.Finding.Status)} | {r.Finding.Evidence.Replace("|", "/")} | {r.Check.Chapters} | {r.Check.Owner} |");
        }

        File.WriteAllText(Path.Combine(folder, "scorecard.md"), markdown.ToString());
        File.WriteAllText(Path.Combine(folder, "scorecard.json"), JsonSerializer.Serialize(new
        {
            reviewed = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            header,
            decision = Decision(results),
            checks = results.Select(r => new { r.Check.Id, r.Check.Area, r.Check.Name, status = Label(r.Finding.Status), r.Finding.Evidence, r.Check.Chapters, r.Check.Owner, seconds = Math.Round(r.Seconds, 1) }),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
}
