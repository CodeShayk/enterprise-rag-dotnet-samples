using System.Runtime.CompilerServices;
using Ch04.Architecture.Cli;
using Ch04.Knowledge.Core;
using Ch04.Knowledge.Core.Ports;
using Contoso.Corpus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ch04.Tests;

/// <summary>
/// The <see cref="IRetriever"/> contract of ADR-0002 (docs/adr), run against every adapter the
/// composition root can choose.
/// </summary>
public sealed class RetrieverContractTests
{
    public static TheoryData<string> Retrievers => ["Vector", "Keyword"];

    [Theory]
    [MemberData(nameof(Retrievers))]
    public async Task A_retriever_returns_only_chunks_the_asker_may_read(string retriever)
    {
        await using var services = await Compose(retriever);
        var index = services.GetRequiredService<IRetriever>();

        foreach (var question in SmokeSet.Load())
        {
            foreach (var person in People.All)
            {
                var hits = await index.RetrieveAsync(question.Question, person, top: 20, TestContext.Current.CancellationToken);
                Assert.All(hits, hit => Assert.True(person.CanRead(hit.Chunk.Document), $"{retriever} gave {person.Name} {hit.Chunk.Id}"));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Retrievers))]
    public async Task A_retriever_finds_the_torque_table_for_Tomas(string retriever)
    {
        await using var services = await Compose(retriever);

        var hits = await services.GetRequiredService<IRetriever>().RetrieveAsync(
            "What torque applies to the M12 flange bolts on P-201?", People.Tomas, top: 4, TestContext.Current.CancellationToken);

        Assert.Contains(hits, hit => hit.Chunk.Document.Id == "CP200-MAN-2009-6.3");
    }

    [Fact]
    public async Task The_pipeline_declines_without_calling_the_model_when_nothing_is_readable()
    {
        var pipeline = new RagPipeline(new EmptyRetriever(), new KeepAll(), new ExplodingGenerator(), new PipelineOptions());

        var answer = await pipeline.AskAsync("What is the CEO's salary?", People.Riley, TestContext.Current.CancellationToken);
        var text = string.Concat(await answer.Text.ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(RagPipeline.NoEvidence, text);
    }

    private static async Task<ServiceProvider> Compose(string retriever)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pipeline:Retriever"] = retriever,
            ["AI:ScriptedChunkDelayMilliseconds"] = "0",
        }).Build();

        var services = new ServiceCollection().AddKnowledgePipeline(configuration).BuildServiceProvider();
        await services.GetRequiredService<IngestionPipeline>().RunAsync(TestContext.Current.CancellationToken);
        return services;
    }

    private sealed class EmptyRetriever : IRetriever
    {
        public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
    }

    private sealed class KeepAll : IReranker
    {
        public Task<IReadOnlyList<RetrievedChunk>> RerankAsync(string question, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken cancellationToken = default) =>
            Task.FromResult(candidates);
    }

    private sealed class ExplodingGenerator : IAnswerGenerator
    {
        public async IAsyncEnumerable<string> GenerateAsync(string question, IReadOnlyList<RetrievedChunk> evidence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("The model must not be called without evidence.");
#pragma warning disable CS0162 // An iterator needs a yield; this one is never reached.
            yield break;
#pragma warning restore CS0162
        }
    }
}

public sealed class BudgetTests
{
    private static readonly LatencyBudget Budget = new(3000, 15000, new Dictionary<string, int> { ["Stages"] = 560 });
    private static readonly MeasuredRates Rates = new("test", PrefillTokensPerSecond: 54, DecodeTokensPerSecond: 17,
        ThreadsPerAnswer: 10, ModelTokensPerO200kToken: 1.0, AnswerTokens: 56);

    [Fact]
    public void The_prompt_allowance_is_what_prefill_can_read_in_the_time_left()
    {
        // (3,000 ms - 560 ms) x 54 tokens per second = 131.76 tokens
        Assert.Equal(131, BudgetMath.PromptTokenAllowance(Budget, Rates));
    }

    [Fact]
    public void A_whole_answer_adds_decoding_to_the_first_token()
    {
        Assert.Equal(0.56 + 540 / 54.0 + 56 / 17.0, BudgetMath.AnswerSeconds(Budget, Rates, promptTokensO200k: 540), precision: 6);
    }
}
