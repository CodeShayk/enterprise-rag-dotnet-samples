using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;

namespace Ch22.Security.Cli;

/// <summary>
/// The production setup the identity command stands in for, compiled but not run: it needs a Microsoft Entra tenant
/// and an app registration. Tokens are validated against the tenant's published signing keys (configuration section
/// <c>AzureAd</c>: instance, tenant ID, client ID and audience, with the client credential in a secret store), and the
/// API asks Microsoft Graph on the user's behalf when a token's groups claim overflowed.
/// </summary>
public static class EntraSetup
{
    public static void AddContosoIdentity(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))
            .EnableTokenAcquisitionToCallDownstreamApi()
            .AddDistributedTokenCaches();
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.MapInboundClaims = false);
        builder.Services.AddAuthorization();
    }

    /// <summary>
    /// The signed-in user's groups, nested ones included, from Graph with a token obtained on behalf of the user (the
    /// on-behalf-of flow). <c>User.Read</c> is enough: groups come back with their IDs only, which is all the ACLs need.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TransitiveGroupIdsAsync(IAuthorizationHeaderProvider tokens, HttpClient http, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var header = await tokens.CreateAuthorizationHeaderForUserAsync(["https://graph.microsoft.com/User.Read"], claimsPrincipal: user, cancellationToken: cancellationToken);
        var (ids, next) = (new List<string>(), (string?)"https://graph.microsoft.com/v1.0/me/transitiveMemberOf");
        while (next is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(header);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            ids.AddRange(page.RootElement.GetProperty("value").EnumerateArray()
                .Where(o => o.GetProperty("@odata.type").GetString() == "#microsoft.graph.group")
                .Select(o => o.GetProperty("id").GetString()!));
            next = page.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }

        return ids;
    }
}
