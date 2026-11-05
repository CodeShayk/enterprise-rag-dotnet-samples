using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ch25.Knowledge.Api;
using Contoso.Corpus;
using Contoso.Knowledge.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ch25.Readiness.Cli;

/// <summary>
/// The reference implementation run in process, as the tests and the review run it: the API's own Program and
/// configuration, a folder of its own, and a signing key made for the run that stands in for the tenant's keys.
/// Settings go in as host settings, so the API reads them while it builds its services, as it would read Aspire's.
/// Any type from the API's assembly names its entry point; Program would clash with this tool's own.
/// </summary>
public sealed class ReferenceDeployment(string runs, IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Assistant>
{
    public const string Issuer = "https://login.microsoftonline.com/6f1c2a40-3b5d-4c1e-9a7f-2d8e0b4c6a19/v2.0";
    public const string Audience = "api://contoso-knowledge";

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string Runs { get; } = Directory.CreateDirectory(runs).FullName;

    public AssistantStack Stack => Services.GetRequiredService<AssistantStack>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var all = new Dictionary<string, string?>
        {
            ["AI:Provider"] = "Scripted",
            ["AI:ScriptedChunkDelayMilliseconds"] = "0",
            ["Assistant:Runs"] = Runs,
            ["Assistant:PseudonymKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(_key),
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Polly"] = "None", // resilience events are counted from metrics instead (ResilienceEvents)
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) all[key] = value;
        foreach (var (key, value) in all) builder.UseSetting(key, value);
        builder.UseContentRoot(AppContext.BaseDirectory); // the API's appsettings.json is copied here; this tool isn't in its folder
        if (services is not null) builder.ConfigureServices(services);
    }

    /// <summary>A token shaped like Microsoft Entra ID's: issuer, audience, tenant, object ID and direct groups.</summary>
    public string Token(Person person, string? tenant = null, byte[]? key = null) => CreateToken(person, key ?? _key, tenant);

    /// <summary>The same token signed with a given key: the local profile's development key, kept in User Secrets.</summary>
    public static string CreateToken(Person person, byte[] key, string? tenant = null) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer,
        Audience = Audience,
        Claims = new Dictionary<string, object>
        {
            ["tid"] = tenant ?? ContosoDirectory.TenantId,
            ["oid"] = ContosoDirectory.ObjectId(person.Id),
            ["name"] = person.Name,
            ["groups"] = ContosoDirectory.Default.DirectGroups(person.Id).ToArray(),
        },
        Expires = DateTime.UtcNow.AddMinutes(30),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
    });

    /// <summary>POST /ask with a bearer token (or none); the reply when the status is 200.</summary>
    public async Task<(int Status, AnswerReply? Reply)> AskAsync(string question, string? token, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/ask") { Content = JsonContent.Create(new AskRequest(question)) };
        if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(message, cancellationToken);
        var reply = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<AnswerReply>(cancellationToken) : null;
        return ((int)response.StatusCode, reply);
    }

    public Task<(int Status, AnswerReply? Reply)> AskAsync(string question, Person asker, CancellationToken cancellationToken = default) =>
        AskAsync(question, Token(asker), cancellationToken);
}
