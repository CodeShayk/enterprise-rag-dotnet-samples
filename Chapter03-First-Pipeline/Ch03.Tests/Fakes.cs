using System.Runtime.CompilerServices;
using Ch03.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ch03.Tests;

/// <summary>A chat client that records what it was sent and streams a fixed answer, with or without a usage report.</summary>
public sealed class RecordingChatClient(string answer = "Tighten them to 85 N·m. [1]", UsageDetails? usage = null) : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add([.. messages]);
        await Task.Yield();
        foreach (var word in answer.Split(' '))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
        }

        if (usage is not null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>An embedding generator that gives every text the same vector, so every chunk ties with every question.</summary>
public sealed class ConstantEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var vector = new float[ChunkRecord.Dimensions];
        vector[0] = 1f;
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(vector))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>The walking skeleton's real composition with offline settings, and fakes swapped in where a test needs them.</summary>
public static class TestServices
{
    public static async Task<ServiceProvider> CreateAsync(
        IChatClient? chat = null, IEmbeddingGenerator<string, Embedding<float>>? embedder = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI:ScriptedChunkDelayMilliseconds"] = "0" })
            .Build();

        var services = new ServiceCollection().AddLogging().AddContosoKnowledge(configuration);

        // The last registration wins, so a fake replaces the configured model.
        if (chat is not null)
        {
            services.AddKeyedSingleton(KnowledgeModels.Answer, chat);
        }

        if (embedder is not null)
        {
            services.AddSingleton(embedder);
        }

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<KnowledgeIndexer>().StartAsync(TestContext.Current.CancellationToken);
        return provider;
    }
}
