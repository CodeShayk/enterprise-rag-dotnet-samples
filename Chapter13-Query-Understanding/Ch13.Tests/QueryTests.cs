using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;

namespace Ch13.Tests;

public sealed class NormalizationTests
{
    [Fact]
    public void Spelling_is_corrected_toward_the_index_and_known_words_are_left_alone()
    {
        var corrector = new SpellingCorrector(["Tighten the flange bolts to the torque in Table 6-2."], knownWords: ["tight"]);

        Assert.Equal("What torque for the flange bolts?", corrector.Correct("What toque for the flange bolts?").Text);
        Assert.Empty(corrector.Correct("Is it tight?").Changes);
        Assert.Empty(corrector.Correct("e217 on PN-4471-C").Changes);
    }

    [Theory]
    [InlineData("PN-4471-C", true)]
    [InlineData("e217", true)]
    [InlineData("M12", true)]
    [InlineData("torque", false)]
    [InlineData("2026", false)]
    public void Identifiers_mix_letters_and_digits(string token, bool expected) => Assert.Equal(expected, Identifiers.IsIdentifier(token));
}

public sealed class FilterTests
{
    private static readonly RuleFilterExtractor Rules = RuleFilterExtractor.FromAssetRecords(
    [
        Chunk(0, "P201-ASSET", "asset-record", ["P-201", "V-114", "CP-200"]),
        Chunk(1, "CP200-MAN-2009", "manual", ["CP-200"]),
    ]);

    [Fact]
    public void An_equipment_tag_brings_the_tags_on_its_asset_record_and_untagged_documents()
    {
        var expanded = Rules.Extract("What torque applies to the flange bolts on P-201?")!;
        var strict = Rules.Extract("What torque applies to the flange bolts on P-201?", expand: false, includeUntagged: false)!;

        Assert.Equal(["P-201", "V-114", "CP-200", RetrievalFilter.Untagged], expanded.EquipmentTags!);
        Assert.Equal(["P-201"], strict.EquipmentTags!);
    }

    [Fact]
    public void Only_document_types_named_in_words_become_filters()
    {
        Assert.Equal(["sop", "work-instruction"], Rules.Extract("What does the SOP say about zero energy?")!.DocumentTypes!);
        Assert.Null(Rules.Extract("Which parts does ECN-2291 affect?"));
    }

    [Fact]
    public void An_untagged_document_passes_only_when_the_filter_asks_for_it()
    {
        var catalog = Chunk(2, "PARTS-CAT-2027", "parts-catalog", []);

        Assert.False(catalog.Matches(new RetrievalFilter { EquipmentTags = ["P-201"] }));
        Assert.True(catalog.Matches(new RetrievalFilter { EquipmentTags = ["P-201", RetrievalFilter.Untagged] }));
    }

    internal static IndexedChunk Chunk(int ordinal, string doc, string type, string[] tags) =>
        new(ordinal, new Chunk(doc, 0, 4, "text"), new SourceDocument(doc, doc, "fluid", ["everyone"], "text"), "records", type, tags, null, null);
}

public sealed class LlmStepTests : IDisposable
{
    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), $"ch13-cache-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public async Task The_cache_answers_a_repeated_prompt_without_the_model()
    {
        var chat = new FakeChat("How do I clear error E-217 on a CP-200?");
        var condenser = new Condenser(chat, new LlmCache(_cachePath));
        var first = await condenser.CondenseAsync(["What does error E-217 mean on a CP-200?"], "And how do I clear it?", TestContext.Current.CancellationToken);

        var reloaded = new Condenser(chat, new LlmCache(_cachePath));
        var second = await reloaded.CondenseAsync(["What does error E-217 mean on a CP-200?"], "And how do I clear it?", TestContext.Current.CancellationToken);

        Assert.Equal("How do I clear error E-217 on a CP-200?", first);
        Assert.Equal(first, second);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task A_rewrite_that_drifts_away_from_the_question_is_thrown_away()
    {
        // The fake embedder places the question and the rewrite at right angles.
        var rewriter = new Rewriter(new FakeChat("Best restaurants near Riverside"), new LlmCache(_cachePath), new FakeEmbedder(), 0.8);

        var result = await rewriter.RewriteAsync("What torque applies to the M12 flange bolts?", TestContext.Current.CancellationToken);

        Assert.Equal("What torque applies to the M12 flange bolts?", result);
        Assert.Equal(1, rewriter.Rejected);
    }

    [Fact]
    public async Task The_planner_stops_at_an_out_of_scope_question()
    {
        var classifier = await IntentClassifier.CreateAsync(new FakeEmbedder(), [("torque of the flange bolts", Intent.InScope), ("weather forecast", Intent.OutOfScope)]);
        var planner = new QueryPlanner(classifier, new SpellingCorrector(["torque"]), null, null, null);

        var plan = await planner.PlanAsync("weather forecast for tomorrow", [], TestContext.Current.CancellationToken);

        Assert.Equal(Intent.OutOfScope, plan.Intent);
        Assert.Equal("none", plan.Route);
        Assert.False(plan.Milliseconds.ContainsKey("spelling"));
    }

    public void Dispose() => File.Delete(_cachePath);

    private sealed class FakeChat(string reply) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Texts about torque or bolts point one way, everything else the other.</summary>
    internal sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v =>
                new Embedding<float>(v.Contains("torque") || v.Contains("bolts") ? new[] { 1f, 0f } : new[] { 0f, 1f }))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
