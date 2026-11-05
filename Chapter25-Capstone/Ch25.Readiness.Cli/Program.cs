using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ch25.Readiness.Cli;
using Contoso.Corpus;
using Contoso.Knowledge.Embeddings;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Trace;

Console.OutputEncoding = Encoding.UTF8;
var runs = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch25"));
var command = args.FirstOrDefault() ?? "review";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

switch (command)
{
    case "review":
        await ReviewAsync(Option("--model") ?? "scripted");
        break;
    case "ablation":
        await Ablation.RunAsync(runs);
        break;
    case "token":
        Token(args.ElementAtOrDefault(1) ?? "tomas.silva");
        break;
    case "ask":
        await AskAsync(Option("--model") ?? "scripted", Option("--as") ?? "tomas.silva",
            Option("--file") is { } file ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(file))! : [args[1]]);
        break;
    default:
        Console.WriteLine("Usage: review [--model llama3.2] | ablation | token <person> | ask <question> [--as <person>] [--model llama3.2] [--file <JSON list of questions>]");
        break;
}

// A token for the local profile, signed with the development key the API reads from its User Secrets.
void Token(string personId)
{
    var secrets = new ConfigurationBuilder().AddUserSecrets("contoso-ch25-knowledge-api").Build();
    if (secrets["Authentication:Schemes:Bearer:SigningKeys:0:Value"] is not { Length: > 0 } key)
    {
        Console.WriteLine("No development key yet. From the sample-code folder:");
        Console.WriteLine($"  dotnet user-secrets set Authentication:Schemes:Bearer:SigningKeys:0:Issuer {ReferenceDeployment.Issuer} --project Chapter25-Capstone/Ch25.Knowledge.Api");
        Console.WriteLine($"  dotnet user-secrets set Authentication:Schemes:Bearer:SigningKeys:0:Value {Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))} --project Chapter25-Capstone/Ch25.Knowledge.Api");
        return;
    }

    Console.WriteLine(ReferenceDeployment.CreateToken(People.Find(personId) ?? throw new ArgumentException($"Unknown person {personId}."), Convert.FromBase64String(key)));
}

// Requests through the reference implementation in a temporary folder, with what the API returns for each.
async Task AskAsync(string model, string personId, IReadOnlyList<string> questions)
{
    var folder = Directory.CreateTempSubdirectory("contoso-ask-").FullName;
    var settings = model == "scripted" ? null : new Dictionary<string, string?> { ["AI:Provider"] = "Ollama", ["AI:OllamaModel"] = model };
    await using (var service = new ReferenceDeployment(folder, settings))
    {
        var asker = People.Find(personId) ?? throw new ArgumentException($"Unknown person {personId}.");
        foreach (var question in questions)
        {
            var (status, reply) = await service.AskAsync(question, asker);
            Console.WriteLine(question);
            Console.WriteLine(reply is null ? $"  HTTP {status}" : $"  {reply.Status}: {reply.Text}");
            if (reply is not null) Console.WriteLine($"  sources: {string.Join(", ", reply.Sources.Select(s => s.DocumentId))}");
        }
    }

    Directory.Delete(folder, recursive: true);
}

// The lab: the reference implementation in process, the four checklists against it, the scorecard published.
async Task ReviewAsync(string model)
{
    var folder = Path.Combine(runs, "review");
    if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    var settings = model == "scripted" ? null : new Dictionary<string, string?> { ["AI:Provider"] = "Ollama", ["AI:OllamaModel"] = model };
    var spans = new List<Activity>();
    await using var service = new ReferenceDeployment(Path.Combine(folder, "service"), settings,
        s => s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(spans)));
    var live = await service.Stack.LiveAsync();
    var parts = await service.Stack.ReadyAsync();
    var context = new ReviewContext(service, parts, parts.Open.Model, folder) { Spans = spans };
    var header = $"Ch25.Knowledge.Api in process: index {live.Version} ({live.Chunks.Count} chunks), model {parts.Open.Model}, prompt {Contoso.Knowledge.Generation.GroundingPrompt.Version}, configuration {parts.Configuration}";
    Console.WriteLine(header);
    // Operability runs last, so the latency and telemetry checks cover every answer the review asked for.
    var results = await Review.RunAsync(context, [.. QualityChecks.All, .. SecurityChecks.All, .. GovernanceChecks.All, .. OperabilityChecks.All]);
    Console.WriteLine($"{Review.Totals(results)}; decision: {Review.Decision(results)}");
    Review.Publish(context, results, Path.Combine(runs, "readiness"), header);
    Console.WriteLine($"Scorecard: {Path.Combine(runs, "readiness", "scorecard.md")}");
}
