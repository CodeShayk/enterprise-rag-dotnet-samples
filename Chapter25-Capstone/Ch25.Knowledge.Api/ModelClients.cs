using System.Security.Cryptography;
using Contoso.AI;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Observability;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Http.Resilience;
using OllamaSharp;
using OllamaSharp.Models;
using Polly;

namespace Ch25.Knowledge.Api;

/// <summary>The provider behind a model and the name provenance and the registry know it by.</summary>
public sealed record ModelIdentity(AIProvider Provider, string Name);

/// <summary>
/// The assistant's models, each under its own key: <c>answer</c> and <c>planner</c> from the <c>AI</c> section, and
/// <c>restricted</c> from <c>AI:Restricted</c>, the model inside Contoso's network that answers whenever the evidence
/// comes from a collection restricted to HR or Procurement (ADR-0001); without that section it's the answer model.
/// A local model gets Chapter 24's pipeline: one retry before the stream starts, a circuit breaker and 20 seconds to
/// the first token. Every model sits behind Chapter 16's breaker, whose exception the loop answers with the evidence
/// alone, and Microsoft.Extensions.AI's OpenTelemetry client, with message content off (Chapter 20).
/// </summary>
public static class ModelClients
{
    public static IServiceCollection AddModels(this IServiceCollection services, IConfiguration configuration)
    {
        var answer = AIClientFactory.ReadOptions(configuration);
        var section = configuration.GetSection("AI:Restricted");
        var restricted = section.Exists() ? Read(section) : answer;
        if (new[] { answer, restricted }.FirstOrDefault(o => o.Provider == AIProvider.Ollama) is { } ollama)
        {
            services.AddHttpClient("ollama", client =>
                {
                    client.BaseAddress = new Uri(ollama.OllamaEndpoint);
                    client.Timeout = Timeout.InfiniteTimeSpan; // the pipeline's timeout governs, not HttpClient's 100 seconds
                })
                .AddResilienceHandler("model", pipeline => pipeline
                    .AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 1, Delay = TimeSpan.FromSeconds(1) })
                    .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    {
                        FailureRatio = 0.5, MinimumThroughput = 4, SamplingDuration = TimeSpan.FromMinutes(1), BreakDuration = TimeSpan.FromSeconds(30),
                    })
                    .AddTimeout(TimeSpan.FromSeconds(20)));
        }

        services.AddKeyedSingleton("answer", Identity(answer));
        services.AddKeyedSingleton("restricted", Identity(restricted));
        services.AddKeyedSingleton("answer", (sp, _) => Create(sp, answer, answers: true));
        services.AddKeyedSingleton("planner", (sp, _) => Create(sp, answer, answers: false));
        services.AddKeyedSingleton("restricted", (sp, _) => section.Exists() ? Create(sp, restricted, answers: true) : sp.GetRequiredKeyedService<IChatClient>("answer"));
        return services;
    }

    private static AIOptions Read(IConfigurationSection section) => AIClientFactory.ReadOptions(new ConfigurationBuilder()
        .AddInMemoryCollection(section.AsEnumerable(makePathsRelative: true).Select(p => new KeyValuePair<string, string?>($"{AIOptions.SectionName}:{p.Key}", p.Value)))
        .Build());

    private static ModelIdentity Identity(AIOptions options) => new(options.Provider, options.Provider switch
    {
        AIProvider.Scripted => "scripted",
        AIProvider.Ollama => $"{options.OllamaModel}@{OllamaDigest(options.OllamaModel)}",
        _ => $"{options.Provider}:{options.Model}",
    });

    private static IChatClient Create(IServiceProvider sp, AIOptions options, bool answers)
    {
        var inner = options.Provider == AIProvider.Ollama
            ? new OllamaApiClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama"), options.OllamaModel)
            : AIClientFactory.CreateChatClient(options, answers ? [ExtractiveScript.Rule, .. ContosoScripts.Default] : null);
        var builder = new ChatClientBuilder(inner);
        if (answers && options.Provider == AIProvider.Ollama) builder.ConfigureOptions(o => o.AddOllamaOption(OllamaOption.NumCtx, 8192));
        return new ModelCircuitBreaker(builder.UseOpenTelemetry(sourceName: RagTelemetry.ModelsSource).Build());
    }

    /// <summary>Ollama's digest for a model: the SHA-256 of its manifest, read from disk (Chapter 23).</summary>
    public static string OllamaDigest(string model)
    {
        var (name, tag) = model.Contains(':') ? (model.Split(':')[0], model.Split(':')[1]) : (model, "latest");
        var manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models", "manifests", "registry.ollama.ai", "library", name, tag);
        return File.Exists(manifest) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifest)))[..12] : "unknown";
    }
}
