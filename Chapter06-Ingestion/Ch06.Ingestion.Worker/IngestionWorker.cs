using Microsoft.Extensions.Hosting;

namespace Ch06.Ingestion.Worker;

/// <summary>
/// The pipeline as a hosted service: one run per interval, set from the freshness the data contracts promise.
/// A failed run is logged and retried at the next interval; the connectors' checkpoints only move when a run succeeds.
/// </summary>
public sealed class IngestionWorker(Workspace workspace, TimeSpan interval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await Commands.RunAsync(workspace, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Run failed; retrying at the next interval: {e.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
