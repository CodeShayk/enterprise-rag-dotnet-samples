using System.Diagnostics;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Ch25.Knowledge.Api;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Observability;
using Contoso.Knowledge.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection(AssistantOptions.SectionName);
builder.Services.Configure<AssistantOptions>(settings);

// Who asks comes from a validated bearer token (Chapter 22). The settings live under Authentication:Schemes:Bearer:
// keys from `dotnet user-jwts` in the local profile, Microsoft Entra ID's authority in the hybrid one.
builder.Services.AddAuthentication().AddJwtBearer(options => options.MapInboundClaims = false);
builder.Services.AddAuthorization();

// A token bucket per tenant, keyed by the validated identity. The counters live in this instance (see Chapter 25).
var perMinute = settings.GetValue("RequestsPerMinute", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("tenant", http => RateLimitPartition.GetTokenBucketLimiter(TenantOf(http.User), _ => new TokenBucketRateLimiterOptions
    {
        TokenLimit = perMinute, TokensPerPeriod = perMinute, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0,
    }));
});

// Answers cached in memory, and in Redis as well when the profile provides one (the hybrid profile's replicas share it).
builder.Services.AddHybridCache();
if (builder.Configuration.GetConnectionString("cache") is { Length: > 0 } redis) builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redis);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddModels(builder.Configuration);
builder.Services.AddSingleton(sp => new AssistantStack(
    sp.GetRequiredService<IOptions<AssistantOptions>>(),
    (sp.GetRequiredKeyedService<ModelIdentity>("answer"), sp.GetRequiredKeyedService<ModelIdentity>("restricted")),
    (sp.GetRequiredKeyedService<IChatClient>("answer"), sp.GetRequiredKeyedService<IChatClient>("planner"), sp.GetRequiredKeyedService<IChatClient>("restricted")),
    sp.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<AssistantStack>>(), sp.GetService<DenseDecorator>()));
builder.Services.AddSingleton<Assistant>();
builder.Services.AddHealthChecks().AddCheck<IndexHealthCheck>("index");

// Traces and metrics (Chapter 20), exported over OTLP when the environment names an endpoint, as Aspire does.
var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("contoso-knowledge-api"))
    .WithTracing(t => t.AddSource(RagTelemetry.Name, RagTelemetry.ModelsSource, GenerationTelemetry.SourceName).AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddMeter(RagTelemetry.Name).AddAspNetCoreInstrumentation());
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) telemetry.UseOtlpExporter();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Load the live index now, so the first question doesn't wait for it; /health says when it's ready.
_ = app.Services.GetRequiredService<AssistantStack>().LiveAsync();
app.MapHealthChecks("/health");

app.MapPost("/ask", async (AskRequest request, ClaimsPrincipal user, Assistant assistant, HttpContext http, CancellationToken cancellationToken) =>
{
    Person asker;
    try
    {
        asker = EntraClaims.ToPerson(user, ContosoDirectory.Default);
    }
    catch (UnauthorizedAccessException e)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status403Forbidden);
    }

    var requestId = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
    return Results.Ok(await assistant.AskAsync(request.Question, asker, requestId, cancellationToken));
}).RequireAuthorization().RequireRateLimiting("tenant");

app.Run();

static string TenantOf(ClaimsPrincipal user)
{
    try
    {
        return EntraClaims.ToPerson(user, ContosoDirectory.Default).Tenant;
    }
    catch (UnauthorizedAccessException)
    {
        return "unknown";
    }
}

/// <summary>Healthy once an index version is loaded and serving.</summary>
public sealed class IndexHealthCheck(AssistantStack stack) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(stack.Current is { } live ? HealthCheckResult.Healthy($"index {live.Version}") : HealthCheckResult.Unhealthy("loading the index"));
}

/// <summary>Visible to WebApplicationFactory in the tests and the readiness review.</summary>
public partial class Program;
