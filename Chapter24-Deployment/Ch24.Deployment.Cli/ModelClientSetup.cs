using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using OllamaSharp;
using Polly;

namespace Ch24.Deployment.Cli;

/// <summary>
/// The model client's own resilience pipeline, compiled but not run against a model here. The standard handler's
/// 10-second attempt timeout and three retries suit short calls, not a CPU model that takes 16 seconds an answer.
/// The pipeline wraps SendAsync, which for a streamed response (OllamaSharp asks for ResponseHeadersRead) completes
/// when the headers arrive, so its timeout and its one retry never touch an answer that has started to stream.
/// </summary>
public static class ModelClientSetup
{
    public static IServiceCollection AddModelClient(this IServiceCollection services, Uri endpoint, string model)
    {
        services.AddHttpClient("model", client =>
            {
                client.BaseAddress = endpoint;
                client.Timeout = Timeout.InfiniteTimeSpan; // the pipeline's timeout governs, not HttpClient's 100 seconds
            })
            .AddResilienceHandler("model", pipeline => pipeline
                .AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 1, Delay = TimeSpan.FromSeconds(1) })
                .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5, MinimumThroughput = 4, SamplingDuration = TimeSpan.FromMinutes(1), BreakDuration = TimeSpan.FromSeconds(30),
                })
                .AddTimeout(TimeSpan.FromSeconds(20))); // until the first token: Chapter 21 measured a median of 9 s
        return services.AddSingleton<IChatClient>(sp => new OllamaApiClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("model"), model));
    }
}
