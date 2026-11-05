using Ch03.Knowledge;
using Contoso.AI;
using Contoso.Corpus;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;

namespace Ch03.Tests;

public sealed class SkeletonTests
{
    private const string Torque = "What torque applies to the M12 flange bolts on P-201?";

    private static readonly IReadOnlyList<SourceDocument> Corpus = MarkdownLoader.LoadFolder(MarkdownCorpus.Folder);

    [Fact]
    public void The_loader_reads_permissions_from_front_matter()
    {
        var guidance = Corpus.Single(d => d.Id == "HR-PL-2027");

        Assert.Equal(["hr", "people-managers"], guidance.AllowedGroups);
        Assert.StartsWith("# HR guidance", guidance.Text);
    }

    [Fact]
    public void Chunks_stay_within_budget_and_overlap()
    {
        var chunker = new FixedSizeChunker(TiktokenTokenizer.CreateForEncoding("o200k_base"), chunkTokens: 100, overlapTokens: 20);

        var chunks = chunker.Split(Corpus.Single(d => d.Id == "CP200-TRB-E217")).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(chunk.TokenCount, 1, 100));
        for (var i = 1; i < chunks.Count; i++)
        {
            // Each chunk starts with words the previous chunk ended with.
            Assert.Contains(chunks[i].Text[..25], chunks[i - 1].Text);
        }
    }

    [Fact]
    public async Task Tomas_gets_the_torque_table_first()
    {
        await using var services = await TestServices.CreateAsync();
        var index = services.GetRequiredService<KnowledgeIndex>();

        var hits = await index.SearchAsync(Torque, People.Tomas, top: 4, TestContext.Current.CancellationToken);

        Assert.Equal("CP200-MAN-2009-6.3", hits[0].DocumentId);
    }

    [Fact]
    public async Task Nobody_retrieves_a_chunk_they_may_not_read_even_when_it_matches_perfectly()
    {
        // Every chunk gets the same vector as every question, so similarity can't keep anything out.
        await using var services = await TestServices.CreateAsync(embedder: new ConstantEmbeddingGenerator());
        var index = services.GetRequiredService<KnowledgeIndex>();

        foreach (var person in People.All)
        {
            var hits = await index.SearchAsync("anything", person, top: 100, TestContext.Current.CancellationToken);

            Assert.NotEmpty(hits);
            Assert.All(hits, hit => Assert.True(person.CanRead(Corpus.Single(d => d.Id == hit.DocumentId)),
                $"{person.Name} retrieved {hit.ChunkId}"));
        }
    }

    [Fact]
    public async Task The_measuring_middleware_prefers_the_providers_token_counts()
    {
        var measurements = new ChatMeasurements();
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        var reported = new MeasuringChatClient(
            new RecordingChatClient(usage: new UsageDetails { InputTokenCount = 512, OutputTokenCount = 9 }), measurements, tokenizer);

        await reported.GetStreamingResponseAsync("Hello", cancellationToken: TestContext.Current.CancellationToken)
            .ToChatResponseAsync(TestContext.Current.CancellationToken);

        Assert.Equal((512, 9, true), (measurements.Last!.InputTokens, measurements.Last.OutputTokens, measurements.Last.TokensReported));

        var counted = new MeasuringChatClient(
            new ScriptedChatClient(options: new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero }), measurements, tokenizer);

        await counted.GetResponseAsync("Hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(measurements.Last!.TokensReported);
        Assert.Equal(tokenizer.CountTokens("Hello"), measurements.Last.InputTokens);
    }
}
