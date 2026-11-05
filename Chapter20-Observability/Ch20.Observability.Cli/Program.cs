using System.Text;
using Ch20.Observability.Cli;

// Commands (from the sample-code folder):
//   ingest                                ingestion as one trace: parse, chunk and embed spans
//   why [--id G001]                       one answer's trace as a tree: every span, its time and what it recorded
//   agent [--id A12]                      one of Chapter 17's agent tasks traced: model calls and tool calls as spans
//   record [--split test]                 every question of a split traced: answer records, span times, quality metrics
//   diagnose                              failure points from traces against Chapter 15's 34 labeled failures (dev)
//   replay --id G017 [--expect REGEX]     rerun a recorded answer, compare stage by stage, then turn compression off
//   privacy                               content-capture modes, redaction, and head against tail sampling
//   drift                                 query-mix, embedding and corpus drift; SLO burn rates and alerts
//   backends                              the same telemetry to Jaeger and Prometheus in containers (needs Docker)
// Answers replay from Chapter 14's recordings; new model calls need Ollama with llama3.2. With OTEL_EXPORTER_OTLP_ENDPOINT
// set, traces and metrics also go to that endpoint (the Aspire dashboard, Jaeger, a collector).
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "why";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
if (command == "ingest")
{
    await Ingest.RunAsync();
    return;
}

if (command == "agent")
{
    await Agents.TraceAsync(Option("--id") ?? "A12");
    return;
}

var lab = await Lab.LoadAsync();

await (command switch
{
    "why" => Commands.WhyAsync(lab, Option("--id") ?? "G001"),
    "record" => Commands.RecordAsync(lab, Option("--split") ?? "dev"),
    "diagnose" => Task.Run(() => Commands.Diagnose(lab)),
    "replay" => Commands.ReplayAsync(lab, Option("--id") ?? "G017", Option("--expect")),
    "privacy" => Operations.PrivacyAsync(lab),
    "drift" => Operations.DriftAsync(lab),
    "backends" => Operations.BackendsAsync(lab),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
