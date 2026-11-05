using Ch01.WhyRag;
using Contoso.AI;
using Contoso.Corpus;
using Microsoft.Extensions.AI;

namespace Ch01.Tests;

public sealed class WhyRagTests
{
    private const string Torque = "What torque applies to the M12 flange bolts on P-201?";
    private const string Leave = "What changed in the parental leave policy this year?";

    private static readonly KeywordRetriever Retriever = new(SeedDocuments.All);

    private static ScriptedChatClient Scripted() =>
        new(options: new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero });

    [Fact]
    public void The_torque_table_is_the_only_hit_for_Tomas()
    {
        var hit = Assert.Single(Retriever.Retrieve(Torque, People.Tomas));

        Assert.Equal("CP200-MAN-2009-6.3", hit.Document.Id);
    }

    [Fact]
    public void Restricted_guidance_reaches_Daniel_but_never_Tomas()
    {
        Assert.Equal("HR-PL-2027", Assert.Single(Retriever.Retrieve(Leave, People.Daniel)).Document.Id);
        Assert.Empty(Retriever.Retrieve(Leave, People.Tomas));
    }

    [Fact]
    public async Task The_pipeline_abstains_without_calling_the_model_when_nothing_matches()
    {
        var chat = new CountingChatClient();

        var answer = await new TinyRag(chat, Retriever)
            .AnswerAsync("What is the CEO's salary?", People.Tomas, TestContext.Current.CancellationToken);

        Assert.True(answer.Abstained);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task A_grounded_answer_quotes_the_manual_and_cites_it()
    {
        using var chat = Scripted();

        var answer = await new TinyRag(chat, Retriever)
            .AnswerAsync(Torque, People.Tomas, TestContext.Current.CancellationToken);

        Assert.Contains("to 85 N·m", answer.Text);
        Assert.EndsWith("[1]", answer.Text);
    }

    [Fact]
    public async Task A_model_only_answer_has_neither_the_manual_value_nor_a_citation()
    {
        using var chat = Scripted();

        var text = await new TinyRag(chat, Retriever).AskModelOnlyAsync(Torque, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("85 N·m", text);
        Assert.DoesNotContain("[1]", text);
    }

    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
