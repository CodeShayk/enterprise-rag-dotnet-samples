using Contoso.Corpus;
using Contoso.Knowledge.Agents;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Correction;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Graph;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ch25.Knowledge.Api;

/// <summary>
/// Chapter 19's paths plus passages (ADR-0014), for questions that chain relations: the graph's first two passages go
/// ahead of the re-ranked hybrid's. The graph is viewed through the asker's permissions before anything is ranked. A
/// question takes this route when it names two relations; every other question searches as before.
/// </summary>
public sealed class GraphRoute(KnowledgeGraph graph, IReadOnlyList<IndexedChunk> chunks, IRetriever passages, int fromGraph = 2) : IRetriever
{
    private static readonly HashSet<string> ChainWords =
        ["supplier", "suppliers", "supplies", "supply", "supplied", "vendor", "replaced", "replacement", "replace", "superseded", "supersedes",
         "affected", "affects", "quarantined", "fitted", "installed", "raised", "originator", "later", "follow-up"];

    private readonly GraphRetriever _paths = new(graph, Recognizer, chunks);

    public string Name => passages.Name + ", graph route";

    public static IReadOnlyList<GazetteerEntry> Gazetteer { get; } =
    [
        new("Supplier", "Fabrikam Seals", ["Fabrikam"]),
        new("Supplier", "Litware Bearings", ["Litware"]),
        new("Supplier", "Proseware Castings", ["Proseware"]),
        new("Supplier", "Contoso stores", []),
        new("Customer", "Tailspin Water Utility", ["Tailspin"]),
        new("Customer", "Woodgrove Foods", ["Woodgrove"]),
        new("Site", "Riverside plant", ["Riverside"]),
        new("Site", "Northgate plant", ["Northgate"]),
        new("Site", "Lakeside plant", ["Lakeside"]),
        new("Site", "Eastfield plant", ["Eastfield"]),
        new("Site", "Raw-water pumping station 3", []),
        new("Site", "Treated-water pumping station 1", []),
        new("Site", "Packaging line 2", []),
        new("Site", "Cold store 1", []),
        .. People.All.Select(p => new GazetteerEntry("Person", p.Name, [p.Name.Split(' ')[0]])),
    ];

    public static EntityRecognizer Recognizer { get; } = new(Gazetteer);

    /// <summary>Two different words that point at relations: a shape, found on Chapter 19's multi-hop questions, not a trained router.</summary>
    public static bool IsMultiHop(string question) =>
        question.ToLowerInvariant().Split([' ', ',', '?', '.', '\'', '’'], StringSplitOptions.RemoveEmptyEntries).Distinct().Count(ChainWords.Contains) >= 2;

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var rest = await passages.RetrieveAsync(request, cancellationToken);
        if (!IsMultiHop(request.Question)) return rest;
        var paths = (await _paths.RetrieveAsync(request with { Top = fromGraph }, cancellationToken)).Hits;
        return rest with { Hits = Ranking.DistinctReturns([.. paths, .. rest.Hits], request.Top) };
    }
}

/// <summary>
/// Chapter 17's records step (ADR-0012): a semantic router decides whether a question needs the maintenance records; if
/// so, one Text-to-SQL statement runs through the guard on this asker's views, with one repair if it fails, and its rows
/// join the evidence as a source. Claims that cite the rows get the row check, and the others Chapter 16's verifier.
/// </summary>
public sealed class RecordsRoute(RecordsDatabase database, SemanticRouter router, TableSelector selector, TextToSql sql)
{
    /// <summary>Written for Chapter 17's workflow, none from its task set.</summary>
    private static readonly IReadOnlyList<(string Text, string Route)> Examples =
    [
        ("How many work orders were closed last month?", "records"),
        ("Which work orders are overdue at Northgate?", "records"),
        ("When was the last preventive maintenance on V-114?", "records"),
        ("Who completed the most work orders in December?", "records"),
        ("How many corrective work orders did HG-1102 have this year?", "records"),
        ("Is there an open work order for CNV-12?", "records"),
        ("What is the net price of the PN-3310-A bearing?", "records"),
        ("How many PN-3420-A grease cartridges are in stock?", "records"),
        ("Which parts from Litware Bearings have the longest lead time?", "records"),
        ("Which parts were superseded in 2025?", "records"),
        ("How do I replace the mechanical seal on a CP-200?", "documents"),
        ("What torque applies to the casing bolts?", "documents"),
        ("What does error E-303 mean?", "documents"),
        ("Which oil goes in an HG-400 gearbox?", "documents"),
        ("What does safety bulletin SB-0932 say?", "documents"),
        ("Who must approve a lockout?", "documents"),
        ("How often should the coupling alignment be checked?", "documents"),
        ("What changed in ECN-2240?", "documents"),
        ("Where is pump P-301 installed?", "documents"),
        ("What is the parental leave policy?", "documents"),
    ];

    public static async Task<RecordsRoute> CreateAsync(OnnxEmbeddingModel model, IChatClient chat, Tokenizer tokenizer, string runs)
    {
        var corpus = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "corpus", "sources"));
        var (path, csv, workbook) = (Path.Combine(runs, "records.duckdb"), Path.Combine(corpus, "database", "work-orders.csv"), Path.Combine(corpus, "fileshare", "procurement", "Parts Catalog 2027.xlsx"));
        Directory.CreateDirectory(runs);
        var database = File.Exists(path) ? new RecordsDatabase(path) : await RecordsDatabase.BuildAsync(path, csv, workbook);
        var (passages, queries) = (new OnnxEmbeddingGenerator(model), new OnnxEmbeddingGenerator(model, queries: true));
        return new RecordsRoute(database, await SemanticRouter.CreateAsync(queries, Examples, "documents"), await TableSelector.CreateAsync(passages, queries),
            new TextToSql(chat, new LlmCache(Path.Combine(runs, "text-to-sql.jsonl")), tokenizer));
    }

    /// <summary>The evidence with the query's rows added as a source, or null when the question isn't about the records or no statement ran.</summary>
    public async Task<AssembledContext?> AddRowsAsync(string question, Person asker, AssembledContext context, CancellationToken cancellationToken)
    {
        if (await router.RouteAsync(question, cancellationToken) != "records") return null;
        using var session = database.Open(asker);
        if (session.Views.Count == 0) return null;
        var tables = await selector.SelectAsync(question, session.Views, cancellationToken: cancellationToken);
        var attempt = await sql.RunAsync(question, session, tables, examples: true, repair: true, cancellationToken);
        if (attempt.Result is not { } rows) return null;
        var ledger = new EvidenceLedger();
        ledger.AddPassages(context.Blocks);
        ledger.AddQuery(rows, "Maintenance records: query");
        return context with { Blocks = ledger.Blocks };
    }

    /// <summary>One model call over the evidence, then the row check for claims that cite rows and the verifier for the rest.</summary>
    public static async Task<Answer> AnswerAsync(GroundedAnswerGenerator generator, AnswerVerifier verifier, string question, Person asker, IReadOnlyList<EvidenceBlock> evidence, CancellationToken cancellationToken)
    {
        Answer? answer = null;
        await foreach (var e in generator.StreamAsync(question, asker, evidence: evidence, cancellationToken: cancellationToken))
        {
            if (e is AnswerReady done) answer = done.Answer;
        }

        if (answer is null || answer.Claims.Count == 0) return answer!;
        bool CitesRows(Claim c) => answer.Evidence.FirstOrDefault(b => b.Id == c.Source) is { DocumentId: EvidenceLedger.RecordsDocument };
        var wrong = RowCheck.Unsupported(answer);
        var passages = answer with { Claims = [.. answer.Claims.Where(c => !CitesRows(c))] };
        var repaired = verifier.Repair(passages, verifier.Verify(passages));
        var claims = repaired.Claims.Concat(answer.Claims.Where(c => CitesRows(c) && !wrong.Contains(c))).ToList();
        return claims.Count == 0
            ? repaired with { Status = AnswerStatus.NotFound, Text = GroundedAnswerGenerator.NotFoundText, Claims = [] }
            : repaired with { Status = answer.Status, Text = CitationValidator.Render(claims), Claims = claims };
    }
}
