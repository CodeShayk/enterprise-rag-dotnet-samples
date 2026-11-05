using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Governance;
using Contoso.Knowledge.Observability;
using Contoso.Knowledge.Retrieval;

namespace Ch25.Knowledge.Api;

/// <summary>
/// The request path, in the order the earlier chapters measured it: plan, retrieve under the asker's permissions, add a
/// query's rows if the question is about the records, answer from that evidence through the cache and the verification
/// loop, render, record.
/// </summary>
public sealed class Assistant(AssistantStack stack, TimeProvider time)
{
    public const string DeclinedText = "I can't help with that here. Ask me about Contoso's equipment, procedures, parts or policies.";

    private readonly Lock _provenance = new();

    public async Task<AnswerReply> AskAsync(string question, Person asker, string requestId, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var parts = await stack.ReadyAsync(cancellationToken);
        var live = await stack.LiveAsync(cancellationToken);
        using var span = RagTelemetry.Source.StartActivity("ask");
        span?.SetTag("rag.index.version", live.Version);

        // Chapter 13: unsafe requests and chit-chat stop here, and only after the planner's model confirms the decline.
        if (parts.Planner is { } planner && await planner.PlanAsync(question, [], cancellationToken) is { Retrieve: false })
        {
            var declined = new Answer(AnswerStatus.NotFound, DeclinedText, [], [], 0, "none", new AnswerTimings(0, 0, 0, 0, 0), [], []);
            return Finish(parts, live, question, asker, requestId, declined, "declined", false, "none", clock);
        }

        // Chapters 11, 12 and 22: the hybrid on the live version, trimmed to what the asker may read inside the search,
        // each hit checked again against the live ACLs; then Chapter 14's assembly and compression.
        var result = await parts.Retriever.RetrieveAsync(new RetrievalRequest(question, asker, 4), cancellationToken);
        AssembledContext context;
        using (var assemble = GenerationTelemetry.Source.StartActivity("assemble"))
        {
            context = parts.Assembler.Assemble(result.Hits);
            assemble?.SetTag("rag.evidence.blocks", context.Blocks.Count);
            assemble?.SetTag("rag.evidence.best_probability", context.BestProbability);
        }

        if (!context.IsEmpty)
        {
            using var compress = GenerationTelemetry.Source.StartActivity("compress");
            context = parts.Compressor.Compress(question, context);
            compress?.SetTag("rag.compress.tokens_out", context.Tokens);
        }

        // Chapter 17: a question the router sends to the records gets one guarded query as this asker, and its rows join the evidence.
        var withRows = parts.Records is { } records ? await records.AddRowsAsync(question, asker, context, cancellationToken) : null;
        context = withRows ?? context;

        // ADR-0001: evidence from a collection restricted to HR or Procurement, or from the records, is answered inside Contoso's network.
        var answerer = withRows is not null || context.Blocks.Any(b => parts.Corpus.IsRestricted(b.DocumentId)) ? parts.Restricted : parts.Open;

        // Chapter 21: the model's work is cached by the evidence it reads; Chapter 16: verify, regenerate once, abstain.
        // HybridCache runs the factory without the caller's context when the token can be canceled, so the request's
        // span is handed over, or the model's spans would start a trace of their own.
        var run = new CragRun(question, asker, new BudgetMeter(answerer.Loop.Budget)) { Context = context };
        var caller = Activity.Current;
        var (answer, cached) = context.IsEmpty
            ? ((await answerer.Loop.ContinueAsync(run, CragState.Abstain, cancellationToken)).Answer, false)
            : await answerer.Cache.GetOrAnswerAsync(question, context.Blocks, async token =>
            {
                Activity.Current = caller;
                return withRows is not null
                    ? await RecordsRoute.AnswerAsync(answerer.Generator, parts.Verifier, question, asker, context.Blocks, token)
                    : (await answerer.Loop.ContinueAsync(run, CragState.Generate, token)).Answer;
            }, cancellationToken);
        answer = answer with { Degraded = [.. answer.Degraded.Union(result.Degraded)] };
        return Finish(parts, live, question, asker, requestId, answer, Status(answer), cached, answerer.Model, clock);
    }

    /// <summary>Chapter 22's safe HTML, Chapter 23's provenance record with the configuration hash, Chapter 20's metrics.</summary>
    private AnswerReply Finish(
        AssistantParts parts, ServedIndex live, string question, Person asker, string requestId, Answer answer, string status, bool cached, string answeredBy, Stopwatch clock)
    {
        var rendered = parts.Renderer.Render(answer.Text);
        var model = answer.PromptVersion == "none" ? "none" : answeredBy;
        AnswerProvenanceRecord record;
        lock (_provenance)
        {
            record = parts.Provenance.Append(new AnswerProvenanceRecord
            {
                At = time.GetUtcNow(),
                RequestId = requestId,
                Asker = TelemetryContent.Pseudonym(asker.Id, parts.PseudonymKey),
                Tenant = asker.Tenant,
                QuestionSha = AnswerProvenanceRecord.Sha(question),
                IndexVersion = live.Version,
                Retriever = live.Retriever.Name,
                Evidence = [.. answer.Evidence.Select(b => new EvidenceRef(b.DocumentId, parts.Corpus.Versions.GetValueOrDefault(b.DocumentId, ""),
                    b.Segments.Min(s => s.Start), b.Segments.Max(s => s.End), AnswerProvenanceRecord.Sha(b.Text)[..16]))],
                PromptVersion = answer.PromptVersion,
                Model = model,
                Status = status,
                AnswerSha = AnswerProvenanceRecord.Sha(answer.Text)[..16],
                Claims = [.. answer.Claims.Select(c => new ClaimRef(c.DocumentId ?? "", c.QuoteFound))],
                Configuration = parts.Configuration,
            });
        }

        RagTelemetry.RecordAnswer(answer, asker.Tenant, clock.Elapsed.TotalSeconds);
        return new AnswerReply(status, answer.Text, rendered.Html, rendered.Removed,
            [.. answer.Claims.Select(c => new ClaimReply(c.Text, c.Source, c.Valid, c.DocumentId))],
            [.. answer.Evidence.Select(b => new SourceReply(b.Id, b.DocumentId, b.Title, b.Section, Math.Round(b.Probability, 3)))],
            answer.Degraded, live.Version, answer.PromptVersion, model, cached, record.Sequence, Math.Round(clock.Elapsed.TotalMilliseconds));
    }

    private static string Status(Answer answer) => answer.Status switch
    {
        AnswerStatus.Answered => "answered",
        AnswerStatus.Partial => "partial",
        AnswerStatus.NotFound => "not_found",
        _ => "clarify",
    };
}
