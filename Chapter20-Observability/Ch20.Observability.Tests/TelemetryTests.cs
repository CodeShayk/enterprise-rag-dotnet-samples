using System.Diagnostics;
using System.Diagnostics.Metrics;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Observability;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Ch20.Observability.Tests;

public sealed class TraceTests
{
    private const string Manual = "## 6.3 Flange bolt torque\n\nTighten the M12 flange bolts to 85 N·m.\n";

    private static readonly SourceDocument Document = new("CP200-MAN", "CP-200 manual", "fluid", ["employees"], Manual);

    private static Hit Hit()
    {
        var chunk = new Chunk(Document.Id, 0, Manual.Length, Manual) { Header = "CP-200 manual" };
        return new Hit(new IndexedChunk(0, chunk, Document, "manuals", "manual", ["CP-200"], null, null), 3, "fixed");
    }

    /// <summary>The scripted pipeline's spans for one question, under a request span carrying the tenant as baggage.</summary>
    private static async Task<(Answer Answer, List<Activity> Spans)> TraceAsync()
    {
        var spans = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RagTelemetry.Name, GenerationTelemetry.SourceName, RagTelemetry.ModelsSource)
            .AddProcessor(new BaggageTagProcessor("contoso.tenant"))
            .AddInMemoryExporter(spans).Build();
        var chat = new ChatClientBuilder(new ScriptedChatClient([ExtractiveScript.Rule], new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero }))
            .UseOpenTelemetry(sourceName: RagTelemetry.ModelsSource).Build();
        var generator = new GroundedAnswerGenerator(new TracedRetriever(new FixedRetriever(Hit())), new ContextAssembler(new Calibration(1, 0), TiktokenTokenizer.CreateForEncoding("o200k_base")), chat,
            TiktokenTokenizer.CreateForEncoding("o200k_base"));
        Answer answer;
        using (var request = RagTelemetry.Source.StartActivity("ask"))
        {
            request?.AddBaggage("contoso.tenant", "fluid");
            answer = await generator.AnswerAsync("What torque applies to the M12 flange bolts?", People.Tomas, cancellationToken: TestContext.Current.CancellationToken);
        }

        return (answer, spans);
    }

    [Fact]
    public async Task Every_stage_nests_under_the_answer_across_the_generators_yields()
    {
        var (_, spans) = await TraceAsync();
        Activity Span(string name) => spans.Single(s => s.OperationName.StartsWith(name, StringComparison.Ordinal));

        var answer = Span("answer");
        Assert.Equal(Span("ask").SpanId, answer.ParentSpanId);
        foreach (var child in new[] { "retrieve fixed", "assemble", "chat", "validate" }) Assert.Equal(answer.SpanId, Span(child).ParentSpanId);
        Assert.All(spans.Where(s => s.OperationName != "ask"), s => Assert.Equal("fluid", s.GetTagItem("contoso.tenant")));
    }

    [Fact]
    public async Task The_answer_record_reads_back_from_its_trace_without_the_question()
    {
        var (answer, spans) = await TraceAsync();

        var record = AnswerRecord.FromTrace(spans, "T1", "sha", null, "pseudonym", "fluid");

        Assert.Equal("answered", record.Status);
        Assert.Equal([new SpanRef("CP200-MAN", 0, Manual.Length)], record.Hits);
        Assert.Equal(answer.PromptVersion, record.PromptVersion);
        Assert.NotEmpty(record.Evidence);
        Assert.DoesNotContain(spans.SelectMany(s => s.TagObjects), t => t.Value is string text && text.Contains("M12 flange bolts?", StringComparison.Ordinal));
    }

    [Fact]
    public void Quality_metrics_count_each_answer_by_status_and_each_claim_by_outcome()
    {
        var counts = new Dictionary<string, long>();
        using var listener = new MeterListener { InstrumentPublished = (i, l) => { if (i.Meter.Name == RagTelemetry.Name) l.EnableMeasurementEvents(i); } };
        listener.SetMeasurementEventCallback<long>((i, value, tags, _) =>
        {
            var key = i.Name + string.Concat(tags.ToArray().Where(t => t.Key != "contoso.tenant").Select(t => $"|{t.Value}"));
            counts[key] = counts.GetValueOrDefault(key) + value;
        });
        listener.Start();
        var claims = new[] { new Claim("85 N·m.", "S1", "85 N·m", true, true), new Claim("Star pattern.", "S1", "star", true, false) };

        RagTelemetry.RecordAnswer(new Answer(AnswerStatus.Answered, "x", claims, [], 0.5, "v", new AnswerTimings(1, 2, 3, 4, 5), [], []), "fluid", 3);

        Assert.Equal(1, counts["rag.answers|answered"]);
        Assert.Equal(1, counts["rag.claims|valid"]);
        Assert.Equal(1, counts["rag.claims|invalid"]);
    }

    private sealed class FixedRetriever(params Hit[] hits) : IRetriever
    {
        public string Name => "fixed";

        public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default) => Task.FromResult(RetrievalResult.Of(hits));
    }
}

public sealed class ContentTests
{
    [Fact]
    public void Redaction_removes_people_and_contact_details_and_keeps_identifiers_and_dates()
    {
        Assert.Equal("[person] here, call me on [phone] about E-217", TelemetryContent.Redact("Kwame here, call me on (425) 555-0142 about E-217"));
        Assert.Equal("Send it to [email] by 2026-09-14 for PN-4471-C", TelemetryContent.Redact("Send it to tomas.silva@contoso.example by 2026-09-14 for PN-4471-C"));
        Assert.Null(TelemetryContent.Text("anything", ContentCapture.Off));
    }

    [Fact]
    public void A_pseudonym_is_stable_for_one_key_and_different_under_another()
    {
        var (a, b) = ("key-a"u8.ToArray(), "key-b"u8.ToArray());

        Assert.Equal(TelemetryContent.Pseudonym("tomas.silva", a), TelemetryContent.Pseudonym("tomas.silva", a));
        Assert.NotEqual(TelemetryContent.Pseudonym("tomas.silva", a), TelemetryContent.Pseudonym("tomas.silva", b));
        Assert.Equal(16, TelemetryContent.Pseudonym("tomas.silva", a).Length);
    }
}

public sealed class DiagnosisTests
{
    private static AnswerRecord Record(SpanRef[] hits, SpanRef[] evidence, string[] claims) =>
        new("t", "Q", "sha", null, "p", "fluid", hits, [], evidence, 0.5, "v", "llama3.2", "answered", 1, claims, 1000, 10, 10);

    private static readonly (string, int, int, string)[] Fact = [("DOC", 100, 120, "a")];

    [Fact]
    public void A_fact_is_lost_where_it_stops_appearing()
    {
        Assert.Equal("retrieval", FailurePoints.ForAnswer(Record([new("DOC", 0, 50)], [], []), Fact));
        Assert.Equal("assembly or compression", FailurePoints.ForAnswer(Record([new("DOC", 0, 500)], [new("DOC", 0, 50)], []), Fact));
        Assert.Equal("generation", FailurePoints.ForAnswer(Record([new("DOC", 0, 500)], [new("DOC", 90, 130)], []), Fact));
    }

    [Fact]
    public void A_quote_missing_from_its_source_is_a_citation_failure_and_a_real_quote_needs_a_support_check()
    {
        var record = Record([], [], ["quote-not-found", "valid"]);

        Assert.Equal("citation", FailurePoints.ForClaim(record, 0));
        Assert.Equal("needs a support check", FailurePoints.ForClaim(record, 1));
    }

    [Fact]
    public void Drift_and_burn_rates_compute_as_defined()
    {
        var mix = new Dictionary<string, int> { ["manuals"] = 50, ["records"] = 50 };

        Assert.Equal(0, Drift.Psi(mix, mix), 6);
        Assert.True(Drift.Psi(mix, new Dictionary<string, int> { ["manuals"] = 90, ["records"] = 10 }) > 0.25);
        Assert.Equal(1.0, Drift.BurnRate(5, 100, 0.95), 6);
        Assert.Equal(14.4, Drift.BurnRate(72, 100, 0.95), 6);
    }
}
