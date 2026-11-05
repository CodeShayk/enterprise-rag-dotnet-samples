using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Contoso.Corpus;
using Contoso.Knowledge.Retrieval;
using Contoso.Knowledge.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ch22.Security.Cli;

public sealed record SearchRequest(string Question, string? User = null);

public sealed record SearchReply(string Asker, IReadOnlyList<string> Groups, IReadOnlyList<string> Documents);

/// <summary>
/// The search API behind JWT bearer authentication, in memory. Tokens are shaped like Microsoft Entra ID's (issuer,
/// audience, <c>tid</c>, <c>oid</c>, <c>groups</c>) and signed with a key made for the run, which stands in for the
/// tenant's signing keys; the asker comes from the token and never from the request body.
/// </summary>
public static class IdentityCommand
{
    private const string Audience = "api://contoso-knowledge";
    private static readonly string Issuer = $"https://login.microsoftonline.com/{ContosoDirectory.TenantId}/v2.0";

    public static async Task RunAsync(Lab lab)
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var retriever = await lab.RetrieverAsync(lab.Chunks, rerank: false);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = key,
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("tenant-header", http => RateLimitPartition.GetTokenBucketLimiter(http.Request.Headers["X-Tenant"].ToString(), _ => Bucket));
            options.AddPolicy("tenant-identity", http => RateLimitPartition.GetTokenBucketLimiter(TenantOf(http.User), _ => Bucket));
        });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapPost("/limited/header", () => Results.Ok()).RequireAuthorization().RequireRateLimiting("tenant-header");
        app.MapPost("/limited/identity", () => Results.Ok()).RequireAuthorization().RequireRateLimiting("tenant-identity");
        app.MapPost("/search", async (SearchRequest request, ClaimsPrincipal user, bool? expand) =>
        {
            Person asker;
            try
            {
                asker = expand == false
                    ? People.All.Single(p => ContosoDirectory.ObjectId(p.Id) == user.FindFirst("oid")!.Value) with
                    {
                        Groups = user.FindAll("groups").Select(c => ContosoDirectory.Default.NameOf(c.Value)).ToHashSet(),
                    }
                    : EntraClaims.ToPerson(user, ContosoDirectory.Default);
            }
            catch (UnauthorizedAccessException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status403Forbidden);
            }

            var hits = (await retriever.RetrieveAsync(new RetrievalRequest(request.Question, asker, 4))).Hits;
            return Results.Ok(new SearchReply(asker.Id, [.. asker.Groups.Order()], [.. hits.Select(h => h.Chunk.Document.Id).Distinct()]));
        }).RequireAuthorization();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var question = lab.Questions.Single(q => q.Id == "G017").Question;
        Console.WriteLine($"POST /search \"{question}\"");
        Console.WriteLine($"{"Request",-46} {"Status",6}  Asker, groups and documents");
        async Task Send(string name, string? token, string? bodyUser = null, string query = "")
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/search" + query) { Content = JsonContent.Create(new SearchRequest(question, bodyUser)) };
            if (token is not null) message.Headers.Authorization = new("Bearer", token);
            using var response = await client.SendAsync(message);
            var detail = response.StatusCode == HttpStatusCode.OK && await response.Content.ReadFromJsonAsync<SearchReply>() is { } r
                ? $"{r.Asker} [{string.Join(", ", r.Groups)}] -> {(r.Documents.Count == 0 ? "no documents" : string.Join(", ", r.Documents))}"
                : "";
            Console.WriteLine($"{name,-46} {(int)response.StatusCode,6}  {detail}");
        }

        string Token(Person p, bool overage = false, string? tenant = null)
        {
            var claims = new Dictionary<string, object>
            {
                ["tid"] = tenant ?? ContosoDirectory.TenantId,
                ["oid"] = ContosoDirectory.ObjectId(p.Id),
                ["name"] = p.Name,
            };
            if (overage)
            {
                claims["_claim_names"] = new Dictionary<string, object> { ["groups"] = "src1" };
                claims["_claim_sources"] = new Dictionary<string, object> { ["src1"] = new Dictionary<string, object> { ["endpoint"] = "https://graph.microsoft.com/v1.0/users/me/transitiveMemberOf" } };
            }
            else
            {
                claims["groups"] = ContosoDirectory.Default.DirectGroups(p.Id).ToArray();
            }

            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Audience,
                Claims = claims,
                Expires = DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            });
        }

        var tomas = Token(People.Tomas);
        await Send("Tomás, groups claim expanded", tomas);
        await Send("Tomás, groups claim taken as it is", tomas, query: "?expand=false");
        await Send("Tomás, body says grace.liu", tomas, bodyUser: "grace.liu");
        await Send("no token", null, bodyUser: "grace.liu");
        await Send("Tomás, signature altered", tomas[..^4] + (tomas[^4] == 'A' ? "BBBB" : "AAAA"));
        await Send("a token from another tenant", Token(People.Tomas, tenant: Guid.Empty.ToString()));
        await Send("Grace, groups overage, looked up", Token(People.Grace, overage: true));

        async Task<string> Burst(string path, string token, int count)
        {
            var codes = new List<HttpStatusCode>();
            for (var i = 0; i < count; i++)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, path);
                message.Headers.Authorization = new("Bearer", token);
                message.Headers.Add("X-Tenant", $"t{i}");
                codes.Add((await client.SendAsync(message)).StatusCode);
            }

            return $"{codes.Count(c => c == HttpStatusCode.OK)} served, {codes.Count(c => c == HttpStatusCode.TooManyRequests)} refused (429)";
        }

        Console.WriteLine("Token bucket per tenant (10 requests, 10 more a minute); Tomás sends 15 with a new X-Tenant header each");
        Console.WriteLine($"  partitioned by the header (Chapter 21):         {await Burst("/limited/header", tomas, 15)}");
        Console.WriteLine($"  partitioned by the validated identity's tenant: {await Burst("/limited/identity", tomas, 15)}");
        Console.WriteLine($"  then Nadia (fluid): {await Burst("/limited/identity", Token(People.Nadia), 1)}; Kwame (service): {await Burst("/limited/identity", Token(People.Kwame), 1)}");
    }

    private static TokenBucketRateLimiterOptions Bucket => new() { TokenLimit = 10, TokensPerPeriod = 10, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0 };

    /// <summary>The tenant of the validated asker; the partition a caller can't choose.</summary>
    private static string TenantOf(ClaimsPrincipal user)
    {
        try
        {
            return EntraClaims.ToPerson(user, ContosoDirectory.Default).Tenant;
        }
        catch (UnauthorizedAccessException)
        {
            return "refused";
        }
    }
}
