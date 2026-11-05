using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Observability;
using Contoso.Knowledge.Retrieval;
using DotNet.Testcontainers.Builders;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Ch20.Observability.Cli;

/// <summary>Content in telemetry, sampling, drift, SLOs and the backends the telemetry goes to.</summary>
public static class Operations
{
    /// <summary>
    /// What each content-capture mode puts in one trace (G001), what redaction does to questions people really type,
    /// and what head sampling keeps of the traces worth reading against a tail rule.
    /// </summary>
    public static async Task PrivacyAsync(Lab lab)
    {
        var q = lab.Questions.Single(x => x.Id == "G001");
        Console.WriteLine($"{"Capture mode",-10} {"bytes of attributes",20}  attributes holding content");
        foreach (var (mode, sensitive) in new[] { (ContentCapture.Off, false), (ContentCapture.Redacted, false), (ContentCapture.Full, true) })
        {
            using var telemetry = new Telemetry();
            var (_, spans) = await Commands.AskAsync(telemetry, await lab.GeneratorAsync(Lab.AnswerChat(sensitive)), q, mode);
            var tags = spans.SelectMany(s => s.TagObjects).ToList();
            var bytes = tags.Sum(t => Encoding.UTF8.GetByteCount(t.Key) + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(t.Value)));
            var content = tags.Where(t => t.Key is "rag.question" or "gen_ai.input.messages" or "gen_ai.output.messages" or "gen_ai.system_instructions")
                .Select(t => $"{t.Key} ({Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(t.Value)):N0} B)");
            Console.WriteLine($"{mode,-10} {bytes,20:N0}  {string.Join(", ", content.DefaultIfEmpty("none"))}");
        }

        Console.WriteLine("\nRedaction:");
        foreach (var text in new[]
        {
            "Kwame here, call me on (425) 555-0142 about the E-217 trips at Tailspin",
            "Send the seal change steps for P-201 to tomas.silva@contoso.example",
            "Did Nadia approve ECN-2291 before the 2026-09-14 effective date?",
        })
        {
            Console.WriteLine($"  {text}\n    -> {TelemetryContent.Redact(text)}");
        }

        // Head sampling decides at the root, by trace ID, before anything is known; a tail rule decides at the end.
        var records = Commands.Records("dev").Concat(Commands.Records("test")).ToList();
        bool Worth(AnswerRecord r) => r.Status == "not_found" || r.BestProbability < RagTelemetry.WeakBelow || r.ClaimOutcomes.Any(o => o != "valid");
        bool Head(AnswerRecord r) => ulong.Parse(r.TraceId[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / (double)ulong.MaxValue < 0.10;
        var worth = records.Where(Worth).ToList();
        Console.WriteLine($"\nSampling {records.Count} traces, {worth.Count} worth reading (abstained, weak evidence or a claim not valid):");
        Console.WriteLine($"  head, 10% by trace ID: {records.Count(Head)} kept, {worth.Count(Head)} of the {worth.Count} worth reading");
        Console.WriteLine($"  tail, all worth reading and 10% of the rest: {worth.Count + records.Except(worth).Count(Head)} kept, all {worth.Count}");
    }

    /// <summary>A week of new questions after a product launch: none of the golden set's, all about things the corpus doesn't cover.</summary>
    private static readonly string[] Launch =
    [
        "What torque applies to the VX-90 actuator mounting bolts?", "How do I calibrate the VX-90 position sensor?",
        "Which fuse does the VX-90 actuator take?", "What does fault A-14 mean on a VX-90?", "How often should the VX-90 gearbox be greased?",
        "Can the VX-90 replace the actuator on V-114?", "Who supplies VX-90 spare parts?", "What is the lead time of the VX-90 seal kit?",
        "Is there a VX-90 training course for night shift technicians?", "What PPE do I need to commission a VX-90?",
        "How do I reset a VX-90 after a power loss?", "What is the duty cycle of the VX-90 at 40 °C?",
    ];

    /// <summary>
    /// Drift against the dev questions as a baseline: where the top hit comes from (query mix), how new the questions
    /// are to the embedding model, how often the evidence is weak; the corpus against its index snapshot; SLO burn rates.
    /// </summary>
    public static async Task DriftAsync(Lab lab)
    {
        var retriever = await lab.RetrieverAsync();
        using var bge = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);
        var generator = new OnnxEmbeddingGenerator(bge, queries: true);
        async Task<(Dictionary<string, int> Mix, List<float[]> Vectors, int Weak)> Profile(IReadOnlyList<(string Question, Person Asker)> items)
        {
            var (mix, weak) = (new Dictionary<string, int>(), 0);
            foreach (var (question, asker) in items)
            {
                var hits = (await retriever.RetrieveAsync(new RetrievalRequest(question, asker, 4))).Hits;
                var key = hits.Count == 0 ? "no hit" : hits[0].Chunk.Collection;
                mix[key] = mix.GetValueOrDefault(key) + 1;
                weak += hits.Count == 0 || Lab.Calibration.Probability(hits[0].Score) < RagTelemetry.WeakBelow ? 1 : 0;
            }

            var vectors = (await generator.GenerateAsync(items.Select(i => i.Question))).Select(e => e.Vector.ToArray()).ToList();
            return (mix, vectors, weak);
        }

        (string, Person) Item(GoldenQuestion q) => (q.Question, People.Find(q.Asker)!);
        var baseline = await Profile([.. lab.Questions.Where(q => q.Split == "dev").Select(Item)]);
        var weeks = new (string Name, IReadOnlyList<(string, Person)> Items)[]
        {
            ("test questions", [.. lab.Questions.Where(q => q.Split == "test").Select(Item)]),
            ("test + VX-90 launch", [.. lab.Questions.Where(q => q.Split == "test").Select(Item), .. Launch.Select(t => (t, People.Tomas))]),
            ("VX-90 questions alone", [.. Launch.Select(t => (t, People.Tomas))]),
        };
        Console.WriteLine($"{"Week",-22} {"n",4} {"query-mix PSI",14} {"novel (< 0.75)",15} {"weak evidence",14}");
        Console.WriteLine($"{"dev (baseline)",-22} {baseline.Vectors.Count,4} {"",14} {"",15} {baseline.Weak / (double)baseline.Vectors.Count,14:P0}");
        foreach (var (name, items) in weeks)
        {
            var week = await Profile(items);
            var novel = Drift.NearestSimilarity(baseline.Vectors, week.Vectors).Count(s => s < 0.75);
            Console.WriteLine($"{name,-22} {items.Count,4} {Drift.Psi(baseline.Mix, week.Mix),14:F3} {novel / (double)items.Count,15:P0} {week.Weak / (double)items.Count,14:P0}");
        }

        // Corpus drift: what the index was built from against what the register says now.
        var snapshot = new Dictionary<string, string>();
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "2") continue;
            snapshot[entry.Id] = $"{entry.Version}:{entry.Sha256[..12]}";
        }

        var now = GeneratedCorpus.Register().Where(e => e.CopyOf is null && lab.Documents.ContainsKey(e.Id) && (e.Id != "SOP-FS-014" || e.Version == "3"))
            .ToDictionary(e => e.Id, e => $"{e.Version}:{e.Sha256[..12]}");
        var changed = now.Where(n => snapshot.GetValueOrDefault(n.Key) != n.Value).Select(n => $"{n.Key} {snapshot.GetValueOrDefault(n.Key, "new")} -> {n.Value}").ToList();
        Console.WriteLine($"\nCorpus against an index built before SOP-FS-014 version 3: {changed.Count} of {now.Count} documents changed: {string.Join("; ", changed)}");

        // SLOs on the recorded answers: each answer is good or bad for each objective.
        Console.WriteLine($"\n{"SLO",-46} {"target",7} {"bad",5} {"burn rate",10}  alert");
        var records = Commands.Records("dev").Concat(Commands.Records("test")).ToList();
        foreach (var (name, target, bad) in new (string, double, Func<AnswerRecord, bool>)[]
        {
            ("answer within 15 s (Chapter 4's target)", 0.95, r => r.TotalMs > 15_000),
            ("every citation valid", 0.90, r => r.ClaimOutcomes.Any(o => o is "invalid" or "quote-not-found" or "no-source")),
            ("evidence not weak", 0.90, r => r.Status != "not_found" && r.BestProbability < RagTelemetry.WeakBelow),
        })
        {
            var n = records.Count(bad);
            var burn = Drift.BurnRate(n, records.Count, target);
            var alert = burn >= 14.4 ? "page (14.4x)" : burn >= 6 ? "page (6x)" : burn >= 1 ? "ticket (1x)" : "within budget";
            Console.WriteLine($"{name,-46} {target,7:P0} {n,5} {burn,10:F1}  {alert}");
        }
    }

    /// <summary>
    /// The same telemetry in two real backends in containers: traces to Jaeger 2 over OTLP, metrics to Prometheus 3's
    /// OTLP receiver; then each one is asked what it received.
    /// </summary>
    public static async Task BackendsAsync(Lab lab)
    {
        await using var jaeger = new ContainerBuilder("jaegertracing/jaeger:2.21.0")
            .WithPortBinding(16686, true).WithPortBinding(4318, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(16686))).Build();
        await using var prometheus = new ContainerBuilder("prom/prometheus:v3.13.4")
            .WithCommand("--config.file=/etc/prometheus/prometheus.yml", "--web.enable-otlp-receiver")
            .WithPortBinding(9090, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/-/ready"))).Build();
        await Task.WhenAll(jaeger.StartAsync(), prometheus.StartAsync());
        var (jaegerUi, jaegerOtlp, prom) = ($"http://localhost:{jaeger.GetMappedPublicPort(16686)}", $"http://localhost:{jaeger.GetMappedPublicPort(4318)}", $"http://localhost:{prometheus.GetMappedPublicPort(9090)}");

        string traceId;
        using (var telemetry = new Telemetry(
            t => t.AddOtlpExporter(o => { o.Endpoint = new Uri($"{jaegerOtlp}/v1/traces"); o.Protocol = OtlpExportProtocol.HttpProtobuf; }),
            m => m.AddOtlpExporter(o => { o.Endpoint = new Uri($"{prom}/api/v1/otlp/v1/metrics"); o.Protocol = OtlpExportProtocol.HttpProtobuf; })))
        {
            var generator = await lab.GeneratorAsync(Lab.AnswerChat());
            var (_, spans) = await Commands.AskAsync(telemetry, generator, lab.Questions.Single(x => x.Id == "G001"));
            foreach (var id in new[] { "G012", "G2-T047" }) await Commands.AskAsync(telemetry, generator, lab.Questions.Single(x => x.Id == id));
            traceId = spans[0].TraceId.ToString();
            telemetry.Tracer.ForceFlush();
            telemetry.Meters.ForceFlush();
        }

        using var http = new HttpClient();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var response = await http.GetAsync($"{jaegerUi}/api/traces/{traceId}");
            if (response.IsSuccessStatusCode)
            {
                var trace = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")[0];
                var names = trace.GetProperty("spans").EnumerateArray().Select(s => s.GetProperty("operationName").GetString()).ToList();
                Console.WriteLine($"Jaeger 2.21.0: trace {traceId[..8]}… has {names.Count} spans from service {trace.GetProperty("processes").EnumerateObject().First().Value.GetProperty("serviceName").GetString()}: {string.Join(", ", names.Distinct().Take(6))}, …");
                break;
            }

            await Task.Delay(500);
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await http.GetFromJsonAsync<JsonElement>($"{prom}/api/v1/query?query=rag_answers_total");
            var series = result.GetProperty("data").GetProperty("result");
            if (series.GetArrayLength() > 0)
            {
                Console.WriteLine($"Prometheus 3.13.4: rag_answers_total has {series.GetArrayLength()} series: " +
                    string.Join("; ", series.EnumerateArray().Select(s => $"{s.GetProperty("metric").GetProperty("rag_answer_status").GetString()} = {s.GetProperty("value")[1].GetString()}")));
                break;
            }

            await Task.Delay(500);
        }
    }
}
