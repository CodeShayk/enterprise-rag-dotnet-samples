using Ch16.Corrective.Cli;
using Contoso.Corpus;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Generation;
using Microsoft.Extensions.AI;

namespace Ch16.Tests;

public sealed class CorrectionTests
{
    private static EvidenceBlock Block(string id, string doc, string text) =>
        new(id, doc, doc, doc, "manual", null, [new EvidenceSegment(text, 0, text.Length)], 0.9);

    private static readonly EvidenceBlock[] Evidence =
    [
        Block("S1", "CP200-MAN-2009", "Tighten the M12 flange bolts to 85 N·m."),
        Block("S2", "SB-0932", "Apply lockout/tagout before opening the seal housing."),
    ];

    private static Answer Answered(params Claim[] claims) =>
        new(AnswerStatus.Answered, CitationValidator.Render(claims), claims, Evidence, 1, "test", new AnswerTimings(0, 0, 0, 0, 0), [], []);

    [Fact]
    public void The_budget_stops_retries_and_calls_and_says_which_limit_it_hit()
    {
        var meter = new BudgetMeter(new LoopBudget(MaxRetries: 1, MaxModelCalls: 1));

        meter.Retry();
        meter.Spend(promptTokens: 900, recordedSeconds: 14, wallSeconds: 0);   // replayed: no wall time spent

        Assert.False(meter.CanRetry);
        Assert.False(meter.CanCallModel(900));
        Assert.Equal("retries", meter.Exhausted);
        Assert.InRange(meter.Seconds, 14, 15);   // a replayed call counts the time it took when it was recorded
    }

    [Theory]
    [InlineData(0.60, EvidenceGrade.Correct)]
    [InlineData(0.05, EvidenceGrade.Ambiguous)]
    [InlineData(0.001, EvidenceGrade.Incorrect)]
    public async Task The_calibrated_grader_reads_the_best_probability_against_two_thresholds(double best, EvidenceGrade grade)
    {
        var verdict = await new CalibratedEvidenceGrader(0.1, 0.005).GradeAsync("q", new AssembledContext(Evidence, 100, best, []), TestContext.Current.CancellationToken);

        Assert.Equal(grade, verdict.Grade);
    }

    [Fact]
    public async Task The_LLM_grader_maps_its_three_verdicts_to_CRAG_grades()
    {
        var chat = new FixedChatClient("""{"verdict": "partly", "reason": "The table lists M10 to M16, not M20."}""");

        var verdict = await new LlmEvidenceGrader(chat).GradeAsync("What torque for M20 bolts?", new AssembledContext(Evidence, 100, 0.6, []), TestContext.Current.CancellationToken);

        Assert.Equal(EvidenceGrade.Ambiguous, verdict.Grade);
        Assert.Contains("<evidence>\n[S1]", chat.Last[1].Text);
    }

    [Fact]
    public void Repair_recites_what_another_block_supports_and_drops_what_nothing_supports()
    {
        var answer = Answered(
            new Claim("Tighten the M12 flange bolts to 85 N·m.", "S1", "85 N·m", true, true),
            new Claim("Lock out the motor first.", "S1", "", true, false),
            new Claim("Use a torque wrench calibrated this year.", "S2", "", true, false));
        var verification = new Verification([new ClaimCheck(0, 0.98, true, null, 0), new ClaimCheck(1, 0.01, false, "S2", 0.91), new ClaimCheck(2, 0.02, false, null, 0.01)]);

        var repaired = new AnswerVerifier(null!).Repair(answer, verification);

        Assert.Equal(["S1", "S2"], repaired.Claims.Select(c => c.Source));
        Assert.Equal(AnswerStatus.Answered, repaired.Status);
        Assert.Contains(repaired.Problems, p => p.StartsWith("dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void Feedback_names_only_the_claims_no_block_supports()
    {
        var answer = Answered(
            new Claim("Tighten the M12 flange bolts to 85 N·m.", "S1", "85 N·m", true, true),
            new Claim("Lock out the motor first.", "S1", "", true, false),
            new Claim("Use a torque wrench calibrated this year.", "S2", "", true, false));
        var verification = new Verification([new ClaimCheck(0, 0.98, true, null, 0), new ClaimCheck(1, 0.01, false, "S2", 0.91), new ClaimCheck(2, 0.02, false, null, 0.01)]);

        var feedback = FeedbackRegenerator.Feedback(answer, verification);

        Assert.Contains("\"Use a torque wrench calibrated this year.\" (S2)", feedback);
        Assert.DoesNotContain("Lock out", feedback);   // re-citable, so not the model's problem
        Assert.DoesNotContain("85 N·m", feedback);
    }

    [Fact]
    public void An_answer_with_nothing_supported_left_becomes_not_found()
    {
        var answer = Answered(new Claim("The seal lasts 40,000 hours.", "S1", "", true, false));

        var repaired = new AnswerVerifier(null!).Repair(answer, new Verification([new ClaimCheck(0, 0.01, false, null, 0.02)]));

        Assert.Equal(AnswerStatus.NotFound, repaired.Status);
        Assert.Empty(repaired.Claims);
    }

    [Fact]
    public async Task Only_relevant_evidence_without_a_supported_answer_is_escalated()
    {
        var queue = new ListQueue();
        var policy = new AbstentionPolicy(queue);
        var timings = new AnswerTimings(0, 0, 0, 0, 0);

        var missing = await policy.AbstainAsync("What is the CEO's salary?", People.Tomas, AbstentionReason.NothingRelevant, [], timings, TestContext.Current.CancellationToken);
        var unsupported = await policy.AbstainAsync("What torque for M20 bolts?", People.Tomas, AbstentionReason.NothingSupported, Evidence, timings, TestContext.Current.CancellationToken);

        Assert.Equal(GroundedAnswerGenerator.NotFoundText, missing.Text);
        Assert.EndsWith(AbstentionPolicy.Escalated, unsupported.Text);
        Assert.Equal(["CP200-MAN-2009:0-39", "SB-0932:0-53"], Assert.Single(queue.Items).Sources);
    }

    [Theory]
    [InlineData("Thanks!", EvidenceGrade.Correct, Route.NoRetrieval)]
    [InlineData("Which supplier provides the PN-4471-C seals, and when does the contract end?", EvidenceGrade.Correct, Route.Iterative)]
    [InlineData("What torque applies to the M12 flange bolts on P-201?", EvidenceGrade.Correct, Route.SinglePass)]
    [InlineData("What torque applies to the M20 flange bolts on P-201?", EvidenceGrade.Ambiguous, Route.Iterative)]
    public void The_router_decides_by_the_question_then_by_the_first_grade(string question, EvidenceGrade grade, Route route) =>
        Assert.Equal(route, AdaptiveRouter.After(AdaptiveRouter.Before(question), new EvidenceVerdict(grade, 0.5, "")).Route);

    [Fact]
    public async Task The_circuit_opens_after_three_failures_and_closes_after_the_cooldown()
    {
        var (now, failing) = (DateTimeOffset.UnixEpoch, new FailingChatClient());
        var breaker = new ModelCircuitBreaker(failing, failures: 3, cooldown: TimeSpan.FromSeconds(30), now: () => now);

        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<ModelUnavailableException>(() => breaker.GetResponseAsync("q", cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Equal(3, failing.Calls);   // the fourth call never reached the model
        now += TimeSpan.FromSeconds(31);
        await Assert.ThrowsAsync<ModelUnavailableException>(() => breaker.GetResponseAsync("q", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(4, failing.Calls);   // after the cooldown, one more try
    }

    [Fact]
    public void Every_state_but_done_has_a_way_out_and_done_has_none()
    {
        var from = CragWorkflow.Transitions.Select(t => t.From).ToHashSet();

        Assert.All(Enum.GetValues<CragState>().Where(s => s != CragState.Done), s => Assert.Contains(s, from));
        Assert.DoesNotContain(CragState.Done, from);
    }

    private sealed class ListQueue : IEscalationQueue
    {
        public List<Escalation> Items { get; } = [];

        public ValueTask EnqueueAsync(Escalation escalation, CancellationToken cancellationToken = default)
        {
            Items.Add(escalation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("Connection refused (127.0.0.1:11434)");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedChatClient(string reply) : IChatClient
    {
        public IReadOnlyList<ChatMessage> Last { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Last = [.. messages];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
