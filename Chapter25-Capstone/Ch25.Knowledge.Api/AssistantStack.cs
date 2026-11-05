using System.Text.Json;
using Contoso.AI;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Governance;
using Contoso.Knowledge.Observability;
using Contoso.Knowledge.Operations;
using Contoso.Knowledge.Performance;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;
using Contoso.Knowledge.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Ch25.Knowledge.Api;

/// <summary>The <c>Assistant</c> configuration section.</summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>Where the index catalog, the provenance log and the escalation queue live.</summary>
    public string Runs { get; set; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch25", "service"));

    /// <summary>Chapter 13's intent check; it runs only with a model that can confirm its declines.</summary>
    public bool Planner { get; set; } = true;

    /// <summary>Chapter 17's records step; it runs only with a model that can write its SQL.</summary>
    public bool Records { get; set; } = true;

    public int RequestsPerMinute { get; set; } = 10;

    public List<string> AllowedLinkHosts { get; set; } = [];

    /// <summary>The key behind askers' pseudonyms (a secret, from User Secrets or a key store); without one each run makes its own.</summary>
    public string? PseudonymKey { get; set; }
}

/// <summary>Wraps the dense source of every index version the service loads: how a fault test makes the embedding model fail.</summary>
public sealed record DenseDecorator(Func<IRetriever, IRetriever> Wrap);

/// <summary>A model with its own verification loop and answer cache.</summary>
public sealed record Answerer(CorrectiveRag Loop, GroundedAnswerGenerator Generator, AnswerCache Cache, string Model);

/// <summary>The assembled parts of the request path, built once. <c>Restricted</c> answers from HR and Procurement evidence.</summary>
public sealed record AssistantParts(
    ContosoIndex Corpus, AclStore Acls, IRetriever Retriever, QueryPlanner? Planner, ContextAssembler Assembler, ExtractiveCompressor Compressor,
    Answerer Open, Answerer Restricted, SafeRenderer Renderer, ProvenanceLog Provenance, string Configuration, byte[] PseudonymKey,
    RecordsRoute? Records, AnswerVerifier Verifier);

/// <summary>
/// Builds the assistant from the shared libraries and keeps the index version the live alias names. The first start
/// builds the shipped configuration as version 1 and points the alias at it; after that a release swaps the alias
/// (Chapter 24) and the next request loads the new version.
/// </summary>
public sealed class AssistantStack(
    IOptions<AssistantOptions> options, (ModelIdentity Answer, ModelIdentity Restricted) identities, (IChatClient Answer, IChatClient Planner, IChatClient Restricted) models,
    HybridCache cache, TimeProvider time, ILogger<AssistantStack> logger, DenseDecorator? decorator = null)
{
    public const string AliasName = "live";
    public const string QueryEncoder = "bge-small-en-v1.5-int8";

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _loading = new(1, 1);
    private Task<AssistantParts>? _parts;
    private ServedIndex? _live;

    public AssistantOptions Options => options.Value;

    public IndexCatalog Catalog { get; } = new(Path.Combine(options.Value.Runs, "index"));

    /// <summary>The index version being served, once one is loaded.</summary>
    public ServedIndex? Current => _live;

    public Task<AssistantParts> ReadyAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _parts ??= BuildAsync(options.Value, identities, models, cache, logger);
        return _parts.WaitAsync(cancellationToken);
    }

    public async Task<ServedIndex> LiveAsync(CancellationToken cancellationToken = default)
    {
        var parts = await ReadyAsync(cancellationToken);
        if (_live is { } live && Catalog.Alias(AliasName)?.Version == live.Version) return live;
        await _loading.WaitAsync(cancellationToken);
        try
        {
            var alias = Catalog.Alias(AliasName);
            if (alias is null)
            {
                await parts.Corpus.BuildAsync(Catalog, "v1-shipped", IndexSettings.Shipped);
                alias = Catalog.Swap(AliasName, "v1-shipped", QueryEncoder, time.GetUtcNow());
            }

            if (_live?.Version != alias.Version)
            {
                _live = await parts.Corpus.LoadAsync(Catalog, alias.Version, RetrievalSettings.Shipped, decorator?.Wrap);
                logger.LogInformation("Serving index {Version} ({Chunks} chunks)", alias.Version, _live.Chunks.Count);
            }

            return _live;
        }
        finally
        {
            _loading.Release();
        }
    }

    private async Task<AssistantParts> BuildAsync(
        AssistantOptions settings, (ModelIdentity Answer, ModelIdentity Restricted) identity, (IChatClient Answer, IChatClient Planner, IChatClient Restricted) chat,
        HybridCache answers, ILogger log)
    {
        var corpus = await ContosoIndex.LoadAsync();
        var acls = new AclStore(corpus.Documents.Values);
        var o200k = ContosoIndex.O200k;

        // Retrieval runs on whatever version the alias names now, and every hit is checked again against the live ACLs.
        IRetriever retriever = new TracedRetriever(new VerifiedRetriever(new LiveRetriever(this), acls));

        // Chapter 14's grounded answer (four parents, abstain below 0.005, 500 tokens of the best sentences) inside
        // Chapter 16's shipped loop: verify each claim with NLI, regenerate once with feedback, abstain and escalate.
        var assembler = new ContextAssembler(new Calibration(0.474, -3.543), o200k);
        var compressor = new ExtractiveCompressor(corpus.CrossEncoder, o200k, 500);
        var nli = new OnnxNliModel("DeBERTa-v3 xsmall, 3-way", Path.Combine(EmbeddingModels.Root, "nli-deberta-v3-xsmall"), ["contradiction", "entailment", "neutral"]);
        var escalations = new JsonlEscalationQueue(Path.Combine(settings.Runs, "escalations.jsonl"));
        var verifier = new AnswerVerifier(new ClaimSupport(nli), 0.05, 0.5);
        Answerer Answering(IChatClient model, string name)
        {
            var generator = new GroundedAnswerGenerator(retriever, assembler, model, o200k, compressor: compressor);
            var loop = new CorrectiveRag(retriever, assembler, compressor, generator, new LoopBudget(),
                verifier: verifier,
                regenerator: new FeedbackRegenerator(model, generator.Options),
                abstention: new AbstentionPolicy(escalations));
            return new Answerer(loop, generator, new AnswerCache(answers, GroundingPrompt.Version, name), name);
        }

        var open = Answering(chat.Answer, identity.Answer.Name);
        var restricted = ReferenceEquals(chat.Restricted, chat.Answer) ? open : Answering(chat.Restricted, identity.Restricted.Name);

        // Chapter 13's intent check declines unsafe requests and chit-chat, and only when a model confirms the decline.
        var plannerOn = settings.Planner && identity.Answer.Provider != AIProvider.Scripted;
        QueryPlanner? plan = null;
        if (plannerOn)
        {
            var intents = await IntentClassifier.CreateAsync(new OnnxEmbeddingGenerator(corpus.Model(int8: true)), IntentExamples(), (q, ct) => ConfirmAsync(chat.Planner, q, ct), 0, confirmDeclines: true);
            plan = new QueryPlanner(intents, null, null, null, null, declineOutOfScope: false);
        }

        // Chapter 17's records step writes its SQL with the planner's model, so it runs only when there is one.
        var recordsOn = settings.Records && identity.Answer.Provider != AIProvider.Scripted;
        var records = recordsOn ? await RecordsRoute.CreateAsync(corpus.Model(int8: true), chat.Planner, o200k, settings.Runs) : null;

        var key = settings.PseudonymKey is { Length: > 0 } k ? Convert.FromBase64String(k) : System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        if (settings.PseudonymKey is not { Length: > 0 }) log.LogWarning("No Assistant:PseudonymKey; askers' pseudonyms change with every run.");

        // The effective configuration, hashed into every provenance record so a setting changed in place leaves a trace.
        var configuration = AnswerProvenanceRecord.Sha(JsonSerializer.Serialize(new
        {
            model = identity.Answer.Name,
            restricted = identity.Restricted.Name,
            planner = plannerOn,
            records = recordsOn,
            retrieval = RetrievalSettings.Shipped,
            generation = new { top = 4, abstainBelow = 0.005, calibration = "0.474,-3.543", compressTo = 500, prompt = GroundingPrompt.Version },
            loop = new { support = 0.05, reciteFrom = 0.5, regenerations = 1, budget = open.Loop.Budget },
            settings.RequestsPerMinute,
            links = settings.AllowedLinkHosts.Order(StringComparer.Ordinal),
        }))[..12];

        Directory.CreateDirectory(Path.Combine(settings.Runs, "provenance"));
        return new AssistantParts(corpus, acls, retriever, plan, assembler, compressor, open, restricted,
            new SafeRenderer(settings.AllowedLinkHosts.ToHashSet(StringComparer.OrdinalIgnoreCase)),
            new ProvenanceLog(Path.Combine(settings.Runs, "provenance", "answers.jsonl")), configuration, key, records, verifier);
    }

    /// <summary>Chapter 13's written intent examples, with the in-scope questions written in pairs with the unsafe ones.</summary>
    private static IReadOnlyList<(string Text, Intent Intent)> IntentExamples()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "intents.json")));
        return [.. new[] { "examples", "paired" }.SelectMany(section => document.RootElement.GetProperty(section).EnumerateObject()
            .SelectMany(p => p.Value.EnumerateArray().Select(e => (e.GetString()!, Enum.Parse<Intent>(p.Name)))))];
    }

    /// <summary>Chapter 13's confirmation prompt: one word back from the planner's model.</summary>
    private static async Task<Intent> ConfirmAsync(IChatClient chat, string question, CancellationToken cancellationToken)
    {
        var prompt = $"""
            Classify this message to Contoso's maintenance knowledge assistant. Reply with one word:
            InScope (a question about Contoso's equipment, maintenance, procedures, parts, suppliers or policies),
            OutOfScope (anything else), ChitChat (greetings, thanks, small talk) or Unsafe (a request to defeat a safety
            function or to reach information the person may not see).

            Message: {question}
            """;
        var response = await chat.GetResponseAsync(prompt, new ChatOptions { Temperature = 0, MaxOutputTokens = 5 }, cancellationToken);
        return Enum.GetValues<Intent>().FirstOrDefault(i => response.Text.Contains(i.ToString(), StringComparison.OrdinalIgnoreCase), Intent.InScope);
    }
}

/// <summary>The retriever behind the live alias: each search runs on whichever index version the alias names now.</summary>
public sealed class LiveRetriever(AssistantStack stack) : IRetriever
{
    public string Name => "live";

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default) =>
        await (await stack.LiveAsync(cancellationToken)).Retriever.RetrieveAsync(request, cancellationToken);
}
