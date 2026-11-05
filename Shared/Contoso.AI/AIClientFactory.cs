using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OpenAI;

namespace Contoso.AI;

/// <summary>
/// Creates the raw <see cref="IChatClient"/> and <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>
/// for the configured providers. Callers add middleware (logging, caching, telemetry, function
/// invocation) with <see cref="ChatClientBuilder"/>; this factory only chooses the models.
/// </summary>
public static class AIClientFactory
{
    public static AIOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(AIOptions.SectionName);
        var options = new AIOptions();

        if (Enum.TryParse<AIProvider>(section["Provider"], ignoreCase: true, out var provider))
        {
            options.Provider = provider;
        }

        options.Endpoint = section["Endpoint"] ?? options.Endpoint;
        options.Model = section["Model"] ?? options.Model;
        options.ApiKey = section["ApiKey"] ?? options.ApiKey;
        options.OllamaEndpoint = section["OllamaEndpoint"] ?? options.OllamaEndpoint;
        options.OllamaModel = section["OllamaModel"] ?? options.OllamaModel;

        if (int.TryParse(section["ScriptedChunkDelayMilliseconds"], out var delay) && delay >= 0)
        {
            options.ScriptedChunkDelayMilliseconds = delay;
        }

        if (int.TryParse(section["TimeoutSeconds"], out var timeout) && timeout > 0)
        {
            options.TimeoutSeconds = timeout;
        }

        var embeddings = section.GetSection("Embeddings");
        if (Enum.TryParse<EmbeddingProvider>(embeddings["Provider"], ignoreCase: true, out var embeddingProvider))
        {
            options.Embeddings.Provider = embeddingProvider;
        }

        options.Embeddings.Model = embeddings["Model"] ?? options.Embeddings.Model;
        if (int.TryParse(embeddings["Dimensions"], out var dimensions) && dimensions > 0)
        {
            options.Embeddings.Dimensions = dimensions;
        }

        return options;
    }

    public static IChatClient CreateChatClient(IConfiguration configuration, IEnumerable<ScriptRule>? scriptRules = null) =>
        CreateChatClient(ReadOptions(configuration), scriptRules);

    public static IChatClient CreateChatClient(AIOptions options, IEnumerable<ScriptRule>? scriptRules = null) =>
        options.Provider switch
        {
            AIProvider.Scripted => new ScriptedChatClient(
                scriptRules ?? ContosoScripts.Default,
                new ScriptedChatClientOptions { ChunkDelay = TimeSpan.FromMilliseconds(options.ScriptedChunkDelayMilliseconds) }),

            AIProvider.Ollama => new OllamaApiClient(OllamaHttpClient(options), options.OllamaModel),

            AIProvider.AzureOpenAI => CreateAzureOpenAIClient(options)
                .GetChatClient(Require(options.Model, "AI:Model"))
                .AsIChatClient(),

            AIProvider.OpenAI => CreateOpenAIClient(options)
                .GetChatClient(Require(options.Model, "AI:Model"))
                .AsIChatClient(),

            _ => throw new InvalidOperationException($"Unknown AI provider '{options.Provider}'."),
        };

    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(IConfiguration configuration) =>
        CreateEmbeddingGenerator(ReadOptions(configuration));

    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(AIOptions options) =>
        options.Embeddings.Provider switch
        {
            EmbeddingProvider.Hashing => new HashingEmbeddingGenerator(options.Embeddings.Dimensions),

            EmbeddingProvider.Ollama => new OllamaApiClient(
                OllamaHttpClient(options), Require(options.Embeddings.Model, "AI:Embeddings:Model")),

            EmbeddingProvider.AzureOpenAI => CreateAzureOpenAIClient(options)
                .GetEmbeddingClient(Require(options.Embeddings.Model, "AI:Embeddings:Model"))
                .AsIEmbeddingGenerator(options.Embeddings.Dimensions),

            EmbeddingProvider.OpenAI => CreateOpenAIClient(options)
                .GetEmbeddingClient(Require(options.Embeddings.Model, "AI:Embeddings:Model"))
                .AsIEmbeddingGenerator(options.Embeddings.Dimensions),

            _ => throw new InvalidOperationException($"Unknown embedding provider '{options.Embeddings.Provider}'."),
        };

    private static HttpClient OllamaHttpClient(AIOptions options) => new()
    {
        BaseAddress = new Uri(options.OllamaEndpoint),
        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
    };

    private static OpenAIClient CreateOpenAIClient(AIOptions options) => new(
        new ApiKeyCredential(Require(options.ApiKey, "AI:ApiKey")),
        new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds) });

    // Azure OpenAI's v1 endpoint speaks the OpenAI API, so the OpenAI SDK serves both.
    // Microsoft Entra ID replaces API keys: DefaultAzureCredential uses your developer sign-in
    // locally and a managed identity in Azure.
    private static OpenAIClient CreateAzureOpenAIClient(AIOptions options)
    {
        var endpoint = new Uri(new Uri(Require(options.Endpoint, "AI:Endpoint")), "openai/v1/");
        var authentication = new BearerTokenPolicy(
            new DefaultAzureCredential(),
            "https://cognitiveservices.azure.com/.default");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        return new OpenAIClient(authentication, clientOptions);
    }

    private static string Require(string? value, string key) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Configuration value '{key}' is required for this AI provider.")
            : value;
}
