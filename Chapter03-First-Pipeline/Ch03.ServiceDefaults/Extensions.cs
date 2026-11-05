// Adapted from Aspire's service-defaults project template (aspire-servicedefaults 13.6), MIT License.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// What every Contoso Knowledge service sets up the same way: OpenTelemetry, health checks and
/// service discovery. Each service calls <see cref="AddServiceDefaults{TBuilder}"/> once and
/// <see cref="MapDefaultEndpoints"/> after it builds the app.
/// </summary>
public static class Extensions
{
    public const string HealthEndpointPath = "/health";

    public const string AlivenessEndpointPath = "/alive";

    // Every Contoso.* source and meter: the assistant's model calls (gen_ai spans and token
    // metrics from UseOpenTelemetry) and anything later chapters add.
    private const string ContosoSources = "Contoso.*";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        builder.Services.AddServiceDiscovery();

        // "http://api" resolves to wherever the AppHost started the API. No standard resilience
        // handler here: its timeouts are sized for short calls, not for a streamed answer from a
        // model on a CPU.
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(ContosoSources))
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddSource(ContosoSources)
                .AddAspNetCoreInstrumentation(aspNetCore =>
                    aspNetCore.Filter = context =>
                        !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                        && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                .AddHttpClientInstrumentation());

        // The AppHost sets OTEL_EXPORTER_OTLP_ENDPOINT to its dashboard. Without it, nothing is exported.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
        });

        return app;
    }
}
