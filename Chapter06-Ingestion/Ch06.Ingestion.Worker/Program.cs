using System.Text;
using Ch06.Ingestion.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Commands (from the sample-code folder):
//   reset | run [--parse-workers n] | changes | dedup | asof "<question>" [--date yyyy-mm-dd|none] [--as person] | trace "<sentence>"
//   backfill <parser/version> | quarantine | inventory | dataingestion | watch [--minutes 60] | measure [--split dev]
Console.OutputEncoding = Encoding.UTF8;
var workspace = Workspace.Default;
var command = args.FirstOrDefault() ?? "run";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

switch (command)
{
    case "reset":
        workspace.Reset();
        Console.WriteLine($"Copied the corpus to {workspace.Sources} and started a new store.");
        break;
    case "run":
        await Commands.RunAsync(workspace, cancel.Token, Option("--parse-workers") is { } workers ? int.Parse(workers) : null);
        break;
    case "changes":
        foreach (var change in workspace.SimulateChanges()) Console.WriteLine(change);
        break;
    case "dedup":
        await Commands.DedupAsync(workspace, cancel.Token);
        break;
    case "asof":
        await Commands.AsOfAsync(workspace, args[1], Option("--date"), Option("--as") ?? "tomas.silva", cancel.Token);
        break;
    case "trace":
        await Commands.TraceAsync(workspace, args[1], cancel.Token);
        break;
    case "backfill":
        Commands.Backfill(workspace, args[1]);
        break;
    case "quarantine":
        Commands.Quarantine(workspace);
        break;
    case "inventory":
        Commands.Inventory(workspace);
        break;
    case "dataingestion":
        await DataIngestionMapping.RunAsync(workspace, cancel.Token);
        break;
    case "measure":
        await Commands.MeasureAsync(workspace, Option("--split") ?? "dev", cancel.Token);
        break;
    case "watch":
        // The same pipeline as a hosted service: one run per freshness interval until Ctrl+C.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(workspace);
        builder.Services.AddHostedService(sp => new IngestionWorker(sp.GetRequiredService<Workspace>(), TimeSpan.FromMinutes(double.Parse(Option("--minutes") ?? "60"))));
        await builder.Build().RunAsync(cancel.Token);
        break;
    default:
        Console.WriteLine("Commands: reset | run | changes | dedup | asof | trace | backfill | quarantine | inventory | dataingestion | watch | measure");
        return 1;
}

return 0;
