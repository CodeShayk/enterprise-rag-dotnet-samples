using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.AI;

namespace Ch15.Tests;

public sealed class JudgeAndRegressionTests
{
    /// <summary>A chat client that answers every request with the same text and keeps the last request.</summary>
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

    [Fact]
    public async Task The_judge_reads_a_structured_verdict_and_sees_the_references()
    {
        var chat = new FixedChatClient("""{"score": 4, "reason": "Every fact is there, with one extra detail."}""");

        var verdict = await new LocalJudge(chat).GradeAsync("How soon for a P2 call?", ["| P2 | Production degraded | 1 business day |"], "Within 1 business day.", TestContext.Current.CancellationToken);

        Assert.Equal(new JudgeVerdict(4, "Every fact is there, with one extra detail."), verdict);
        Assert.True(verdict!.Correct);
        Assert.Contains("<reference>\n| P2 | Production degraded | 1 business day |\n</reference>", chat.Last[1].Text);
    }

    [Theory]
    [InlineData("""{"score": 7, "reason": "Off the scale."}""")]
    [InlineData("The answer looks right to me.")]
    public async Task A_verdict_off_the_scale_or_not_in_the_schema_is_unusable(string reply) =>
        Assert.Null(await new LocalJudge(new FixedChatClient(reply)).GradeAsync("q", ["fact"], "answer", TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_pairwise_verdict_is_one_of_three_values()
    {
        var chat = new FixedChatClient("""{"better": "answer_b", "reason": "B gives the interval."}""");

        var verdict = await new LocalJudge(chat).CompareAsync("q", ["fact"], "first", "second", TestContext.Current.CancellationToken);

        Assert.Equal(PairChoice.B, verdict?.Better);
    }

    private static List<ScoredQuestion> Scores(string slice, params double[] scores) =>
        [.. scores.Select((s, i) => new ScoredQuestion($"{slice}{i}", slice, s))];

    [Fact]
    public void A_clear_drop_beyond_the_tolerance_fails_its_slice_and_the_whole()
    {
        var baseline = Scores("numeric", [.. Enumerable.Repeat(1.0, 20)]).Concat(Scores("lookup", [.. Enumerable.Repeat(1.0, 20)])).ToList();
        var candidate = Scores("numeric", [.. Enumerable.Repeat(0.5, 20)]).Concat(Scores("lookup", [.. Enumerable.Repeat(1.0, 20)])).ToList();

        var result = Regression.Compare(baseline, candidate, tolerance: 0.05).ToDictionary(r => r.Slice);

        Assert.True(result["numeric"].Failed);
        Assert.True(result[Regression.All].Failed);
        Assert.False(result["lookup"].Failed);
    }

    [Fact]
    public void A_drop_within_the_tolerance_or_in_a_tiny_slice_passes()
    {
        var baseline = Scores("numeric", [.. Enumerable.Repeat(1.0, 20)]).Concat(Scores("temporal", 1, 1, 1)).ToList();
        var candidate = Scores("numeric", [.. Enumerable.Repeat(1.0, 19), 0.5]).Concat(Scores("temporal", 0, 0, 0)).ToList();

        var result = Regression.Compare(baseline, candidate, tolerance: 0.05).ToDictionary(r => r.Slice);

        Assert.False(result["numeric"].Failed);      // a mean drop of 0.025, under the tolerance
        Assert.False(result["temporal"].Failed);     // three questions can't fail a build
        Assert.Equal(3, result["temporal"].Questions);
    }

    [Fact]
    public void Questions_the_candidate_declined_are_left_out_of_the_pairs()
    {
        var result = Regression.Compare(Scores("lookup", 1, 1, 1, 1, 1), Scores("lookup", 1, double.NaN, 1, 1, 1), tolerance: 0.05);

        Assert.Equal(4, result[0].Questions);
    }
}
