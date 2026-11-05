using System.Diagnostics;
using System.Threading.RateLimiting;

namespace Contoso.Knowledge.Performance;

/// <summary>
/// Model tokens per tenant per day. A request reserves its estimate before the model call and settles what it used
/// afterwards, so concurrent requests can't overspend a quota between them.
/// </summary>
public sealed class TenantTokenBudget(IReadOnlyDictionary<string, long> dailyQuotas, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, (DateOnly Day, long Used)> _used = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public bool TryReserve(string tenant, long tokens)
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
            var (day, used) = _used.GetValueOrDefault(tenant);
            if (day != today) used = 0;
            if (!dailyQuotas.TryGetValue(tenant, out var quota) || used + tokens > quota) return false;
            _used[tenant] = (today, used + tokens);
            return true;
        }
    }

    /// <summary>Gives back what a reservation didn't use (or takes what it underestimated).</summary>
    public void Settle(string tenant, long reserved, long used)
    {
        lock (_lock)
        {
            if (_used.TryGetValue(tenant, out var entry)) _used[tenant] = (entry.Day, Math.Max(0, entry.Used - reserved + used));
        }
    }

    public long Used(string tenant)
    {
        lock (_lock) return _used.GetValueOrDefault(tenant).Used;
    }
}

/// <summary>
/// At most a few model calls at once and a short queue behind them. A request that finds the queue full is told to
/// degrade (sources without an answer, as in Chapter 16) instead of waiting behind answers it can't overtake.
/// </summary>
public sealed class ModelGate(int concurrent, int queue) : IDisposable
{
    private readonly ConcurrencyLimiter _limiter = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = concurrent,
        QueueLimit = queue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    /// <summary>A lease to call the model; check <see cref="RateLimitLease.IsAcquired"/> and degrade when it's false.</summary>
    public ValueTask<RateLimitLease> EnterAsync(CancellationToken cancellationToken = default) => _limiter.AcquireAsync(1, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}

/// <summary>Requests completed in a closed-loop load test, and their latencies.</summary>
public sealed record LoadResult(int Concurrency, double Seconds, IReadOnlyList<double> LatenciesSeconds)
{
    public int Completed => LatenciesSeconds.Count;

    public double PerHour => Completed / Seconds * 3600;

    public double Percentile(double p)
    {
        if (Completed == 0) return double.NaN;
        var sorted = LatenciesSeconds.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}

/// <summary>
/// A closed-loop load test: a fixed number of workers, each sending its next request as soon as the last one returns,
/// until the time is up. Requests started before the deadline finish and count.
/// </summary>
public static class ClosedLoopLoad
{
    public static async Task<LoadResult> RunAsync(int concurrency, TimeSpan duration, Func<int, CancellationToken, Task> request, CancellationToken cancellationToken = default)
    {
        var latencies = new List<double>();
        var next = -1;
        var clock = Stopwatch.StartNew();
        async Task Worker()
        {
            while (clock.Elapsed < duration && !cancellationToken.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);
                var started = clock.Elapsed;
                await request(index, cancellationToken);
                lock (latencies) latencies.Add((clock.Elapsed - started).TotalSeconds);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Worker()));
        return new LoadResult(concurrency, clock.Elapsed.TotalSeconds, latencies);
    }
}
