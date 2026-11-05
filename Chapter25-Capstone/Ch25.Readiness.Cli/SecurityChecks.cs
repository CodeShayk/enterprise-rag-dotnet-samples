using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ch25.Knowledge.Api;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using Contoso.Knowledge.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch25.Readiness.Cli;

/// <summary>Identity, permissions, injected instructions, rendering, the supply chain and limits, tested on the running service.</summary>
public static class SecurityChecks
{
    public static IReadOnlyList<Check> All { get; } =
    [
        new("S1", "security", "Requests without a valid token", "22", "Knowledge Assistant team", TokensAsync),
        new("S2", "security", "The asker comes from the token", "22", "Knowledge Assistant team", AskerAsync),
        new("S3", "security", "No unreadable chunk in anyone's results", "4, 22", "Knowledge Assistant team", LeakageAsync),
        new("S4", "security", "Live permissions over a stale index", "22", "Knowledge Assistant team", LiveAclsAsync),
        new("S5", "security", "Planted instructions quarantined at ingestion", "22", "Knowledge Assistant team", ScreenAsync),
        new("S6", "security", "No remote images or untrusted links rendered", "22", "Knowledge Assistant team", RenderingAsync),
        new("S7", "security", "Model files and digests match their records", "22, 23", "Knowledge Assistant team", SupplyAsync),
        new("S8", "security", "Rate limits per tenant across instances", "21, 22", "Platform team", LimitsAsync),
        new("S9", "security", "Search and the system of record agree on access", "17, 22", "Maintenance systems", ParityAsync),
        new("S10", "security", "HR and Procurement evidence stays inside Contoso", "4, 25", "Knowledge Assistant team", ResidencyAsync),
    ];

    /// <summary>The hybrid profile's routing, with a recording stand-in for the cloud model: which questions would have left?</summary>
    private static async Task<Finding> ResidencyAsync(ReviewContext c)
    {
        var cloud = new RecordingChatClient(AIClientFactory.CreateChatClient(
            new AIOptions { Provider = AIProvider.Scripted, ScriptedChunkDelayMilliseconds = 0 }, [ExtractiveScript.Rule, .. ContosoScripts.Default]));
        await using var service = c.Deploy("residency",
            new Dictionary<string, string?> { ["AI:Provider"] = "Scripted", ["AI:Restricted:Provider"] = "Scripted", ["AI:Restricted:ScriptedChunkDelayMilliseconds"] = "0" },
            s => s.AddKeyedSingleton<IChatClient>("answer", cloud));
        var asked = new (string Id, Person Asker)[] { ("G001", People.Tomas), ("G006", People.Daniel), ("G017", People.Grace) };
        var replies = new List<(string Id, AnswerReply Reply)>();
        foreach (var (id, asker) in asked) replies.Add((id, (await service.AskAsync(c.Question(id).Question, asker)).Reply!));

        var corpus = (await service.Stack.ReadyAsync()).Corpus;
        var left = asked.Where(a => cloud.Prompts.Any(p => p.Contains(c.Question(a.Id).Question, StringComparison.Ordinal))).Select(a => a.Id).ToList();
        var restricted = replies.Where(r => r.Reply.Sources.Any(s => corpus.IsRestricted(s.DocumentId))).Select(r => r.Id).ToList();
        var ok = left.Count > 0 && left.Intersect(restricted).Count() == 0;
        return new(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"the cloud stand-in received {string.Join(", ", left)} only; {string.Join(" and ", restricted)}, citing HR and Procurement documents, went to the restricted model");
    }

    private static async Task<Finding> TokensAsync(ReviewContext c)
    {
        const string question = "What torque applies to the M12 flange bolts on P-201?";
        var token = c.Service.Token(People.Tomas);
        var codes = new (string Name, int Expected, int Actual)[]
        {
            ("no token", 401, (await c.Service.AskAsync(question, (string?)null)).Status),
            ("signature altered", 401, (await c.Service.AskAsync(question, token[..^4] + (token[^4] == 'A' ? "BBBB" : "AAAA"))).Status),
            ("signed with another key", 401, (await c.Service.AskAsync(question, c.Service.Token(People.Tomas, key: RandomNumberGenerator.GetBytes(32)))).Status),
            ("another Entra tenant", 403, (await c.Service.AskAsync(question, c.Service.Token(People.Tomas, tenant: Guid.Empty.ToString()))).Status),
        };
        var wrong = codes.Where(x => x.Expected != x.Actual).ToList();
        return new(wrong.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, string.Join(", ", codes.Select(x => $"{x.Name} {x.Actual}")));
    }

    /// <summary>A body that names another user changes nothing: Kwame's token with Daniel's name gets Kwame's answer.</summary>
    private static async Task<Finding> AskerAsync(ReviewContext c)
    {
        var question = c.Question("G006").Question;
        using var client = c.Service.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/ask")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { question, user = "daniel.mensah" }), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Service.Token(People.Kwame));
        using var response = await client.SendAsync(message);
        var kwame = await response.Content.ReadFromJsonAsync<AnswerReply>();
        if (kwame is not null) c.Answers.Add(kwame);
        var daniel = await c.AskAsync(question, People.Daniel);
        var ok = response.StatusCode == HttpStatusCode.OK && kwame!.Status == "not_found" && daniel.Status is "answered" or "partial";
        return new(ok ? CheckStatus.Pass : CheckStatus.Fail, $"G006 with Kwame's token and Daniel named in the body: {kwame?.Status}; with Daniel's token: {daniel.Status}");
    }

    /// <summary>Every person, every dev question: nothing in the top 4 that the person may not read.</summary>
    private static async Task<Finding> LeakageAsync(ReviewContext c)
    {
        var dev = c.Golden.Where(q => q.Split == "dev").ToList();
        var (searches, unreadable) = (0, 0);
        foreach (var person in People.All)
        {
            foreach (var q in dev)
            {
                var result = await c.Parts.Retriever.RetrieveAsync(new RetrievalRequest(q.Question, person, 4));
                unreadable += result.Hits.Count(h => !c.Parts.Acls.CanRead(person, h.Chunk.Document.Id));
                searches++;
            }
        }

        return new(unreadable == 0 ? CheckStatus.Pass : CheckStatus.Fail, $"{searches} searches ({People.All.Count} people x {dev.Count} dev questions), {unreadable} unreadable hits");
    }

    /// <summary>Revoke Daniel's access at the source and ask before any re-index: the live check must win.</summary>
    private static async Task<Finding> LiveAclsAsync(ReviewContext c)
    {
        var q = c.Question("G006");
        var document = q.Relevant[0].Doc;
        var groups = c.Parts.Acls.GroupsOf(document).ToArray();
        c.Parts.Acls.Set(document, "hr-leadership");
        var revoked = await c.AskAsync(q.Question, People.Daniel);
        c.Parts.Acls.Set(document, groups);
        var restored = await c.AskAsync(q.Question, People.Daniel);
        string Cited(AnswerReply reply) => reply.Sources.Count == 0 ? "no sources" : string.Join(", ", reply.Sources.Select(s => s.DocumentId).Distinct());
        var ok = revoked.Sources.All(s => s.DocumentId != document) && restored.Sources.Any(s => s.DocumentId == document);
        return new(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"{document} revoked at the source: Daniel's answer cited {Cited(revoked)}; restored: {Cited(restored)}");
    }

    private static Task<Finding> ScreenAsync(ReviewContext c)
    {
        var planted = File.ReadLines(ReviewContext.Data("attacks.v1.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).Where(a => a.GetProperty("kind").GetString() == "indirect")
            .Select(a => new SourceDocument(a.GetProperty("document").GetString()!, a.GetProperty("title").GetString()!, "fluid", ["employees"], a.GetProperty("text").GetString()!)).ToList();
        var caught = planted.Count(d => ContosoIndex.Screen(d).Count > 0);
        var quarantined = c.Parts.Corpus.Quarantined(IndexSettings.Shipped);
        var ok = caught == planted.Count && quarantined.Count == 0;
        return Task.FromResult(new Finding(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"{caught} of {planted.Count} planted documents quarantined, {quarantined.Count} of {c.Parts.Corpus.Documents.Count} corpus documents{(quarantined.Count > 0 ? $" ({string.Join(", ", quarantined)})" : "")}"));
    }

    /// <summary>An answer carrying an image whose address holds data, and a link to an unknown host, rendered as the service renders.</summary>
    private static Task<Finding> RenderingAsync(ReviewContext c)
    {
        var rendered = c.Parts.Renderer.Render("Clean the strainer weekly. ![status](https://fabrikam-support.example/p.png?d=badge-4471) [log it](https://fabrikam-support.example/log) [SOP-FS-014](https://intranet.contoso.com/sop/SOP-FS-014)");
        var answersClean = c.Answers.All(a => !a.Html.Contains("<img", StringComparison.OrdinalIgnoreCase) && !a.Html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        var ok = rendered.Removed.Count == 2 && rendered.Html.Contains("intranet.contoso.com", StringComparison.Ordinal) && answersClean;
        return Task.FromResult(new Finding(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"{rendered.Removed.Count} removed (an image carrying data, an unknown host), the intranet link kept; {c.Answers.Count} answers without an image or a script"));
    }

    private static Task<Finding> SupplyAsync(ReviewContext c)
    {
        var checks = ModelProvenance.Verify(Path.Combine(EmbeddingModels.Root, "models.json"), EmbeddingModels.Root);
        var mismatched = checks.Where(m => m.Status.StartsWith("MISMATCH", StringComparison.Ordinal)).ToList();
        var registry = File.ReadAllText(ReviewContext.Data("registry.json"));
        var digest = $"llama3.2@{ModelClients.OllamaDigest("llama3.2")}";
        var ok = mismatched.Count == 0 && registry.Contains($"\"{digest}\"", StringComparison.Ordinal);
        return Task.FromResult(new Finding(ok ? CheckStatus.Pass : CheckStatus.Fail,
            $"{checks.Count(m => m.Status == "matches")} of {checks.Count} model files match their SHA-256; {digest} is {(registry.Contains($"\"{digest}\"", StringComparison.Ordinal) ? "the approved digest" : "not approved")}"));
    }

    /// <summary>One tenant's burst against one instance, then against two: the counters live in each instance.</summary>
    private static async Task<Finding> LimitsAsync(ReviewContext c)
    {
        var settings = new Dictionary<string, string?> { ["Assistant:RequestsPerMinute"] = "3", ["AI:Provider"] = "Scripted" };
        await using var first = c.Deploy("limits-1", settings);
        await using var second = c.Deploy("limits-2", settings);
        async Task<int> Burst(ReferenceDeployment instance)
        {
            var served = 0;
            for (var i = 0; i < 4; i++) served += (await instance.AskAsync("What torque applies to the M12 flange bolts on P-201?", People.Nadia)).Status == 200 ? 1 : 0;
            return served;
        }

        var one = await Burst(first);
        var two = one + await Burst(second);
        return new(two > one ? CheckStatus.Warn : CheckStatus.Pass,
            $"limit 3 a minute: one instance served {one} of 4, two served {two} of 8; the hybrid profile's replicas double a tenant's limit");
    }

    /// <summary>Nadia (Fluid Systems) asks about a Motion Systems gearbox: the CMMS shows her none of its work orders.</summary>
    private static async Task<Finding> ParityAsync(ReviewContext c)
    {
        var result = await c.Parts.Retriever.RetrieveAsync(new RetrievalRequest("Which work orders were raised for gearbox GBX-07?", People.Nadia, 4));
        var rows = result.Hits.Where(h => h.Chunk.DocumentType == "work-orders").Sum(h => Rows(h.Chunk.Chunk.Text, "GBX-07"));
        return new(rows == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            $"Nadia (fluid) reads {rows} GBX-07 work-order rows (motion); the export grants fluid, motion and service, the CMMS each unit its own");
    }

    private static int Rows(string text, string tag) => text.Split('\n').Count(line => line.Contains(tag, StringComparison.Ordinal));
}

/// <summary>A stand-in for a cloud model that remembers every prompt it was sent.</summary>
public sealed class RecordingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public List<string> Prompts { get; } = [];

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(messages);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(messages);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private void Record(IEnumerable<ChatMessage> messages)
    {
        lock (Prompts) Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
    }
}
