using System.Globalization;
using System.Numerics.Tensors;
using Ch06.Ingestion.Worker.Dedup;
using Ch06.Ingestion.Worker.Measure;
using Ch06.Ingestion.Worker.Pipeline;
using Ch06.Ingestion.Worker.Sources;
using Ch06.Ingestion.Worker.Store;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Parsing;
using Microsoft.ML.Tokenizers;
using Polly;
using Polly.Retry;

namespace Ch06.Ingestion.Worker;

public static class Commands
{
    public const string EmbeddingModel = "hashing-384 (Contoso.AI)";
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static async Task<IngestionPipeline> RunAsync(Workspace workspace, CancellationToken cancellationToken, int? parseWorkers = null)
    {
        using var store = new IngestionStore(workspace.Database);
        await using var intranet = await IntranetServer.StartAsync(Path.Combine(workspace.Sources, "intranet"));
        using var intranetHttp = new HttpClient(new RetryHandler { InnerHandler = new HttpClientHandler() }) { BaseAddress = new Uri("http://localhost:5062/") };
        using var graphHttp = new HttpClient(new FakeGraphHandler(workspace.Sources, "contoso-sp")) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };

        var pipeline = new IngestionPipeline(store, ParserRegistry.Default, new StableChunker(Tokenizer), new HashingEmbeddingGenerator(),
            EmbeddingModel, workspace.Artifacts, new LineageEvents(workspace.Folder, store),
            parseWorkers is { } n ? new IngestionOptions { ParseParallelism = n } : null);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var runId = await pipeline.RunAsync(workspace.Connectors(intranetHttp, graphHttp), cancellationToken);
        var elapsed = stopwatch.Elapsed;

        string[] kinds = ["added", "modified", "same text", "touched", "unchanged", "deleted", "quarantined", "needs OCR", "contract violation", "exact duplicate"];
        Console.WriteLine($"Run {runId}: {elapsed.TotalSeconds:F1} s on {Environment.ProcessorCount} logical processors");
        Console.WriteLine();
        Console.WriteLine($"{"Source",-11}" + string.Concat(kinds.Select(k => $"{Header(k),11}")));
        foreach (var system in new[] { "fileshare", "intranet", "sharepoint", "mail", "database" })
        {
            Console.WriteLine($"{system,-11}" + string.Concat(kinds.Select(k => $"{pipeline.Outcomes.Get(system, k),11}")));
        }

        Console.WriteLine();
        Console.WriteLine($"{"Stage",-11}{"Items",7}{"Busy (s)",10}{"Max queue",11}");
        foreach (var stage in pipeline.Stages)
        {
            Console.WriteLine($"{stage.Name,-11}{stage.Items,7}{stage.Busy.TotalSeconds,10:F2}{stage.MaxQueue,11}");
        }

        var (chunks, embedded) = store.ChunkCounts();
        var documents = store.Documents();
        var embeddedNow = new[] { "fileshare", "intranet", "sharepoint", "mail", "database" }.Sum(s => pipeline.Outcomes.Get(s, "chunks embedded"));
        var reused = new[] { "fileshare", "intranet", "sharepoint", "mail", "database" }.Sum(s => pipeline.Outcomes.Get(s, "chunks reused"));
        Console.WriteLine();
        Console.WriteLine($"Store: {documents.Count(d => !d.Tombstoned)} document versions ({documents.Count(d => d.Tombstoned)} tombstoned), " +
            $"{chunks} chunk references, {embedded} distinct embedded chunks. This run embedded {embeddedNow} chunks and reused {reused}.");
        return pipeline;
    }

    private static string Header(string kind) => kind switch
    {
        "contract violation" => "Contract",
        "exact duplicate" => "Duplicate",
        "quarantined" => "Quarantine",
        "same text" => "Same text",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind),
    };

    /// <summary>Exact duplicates from the store, near-duplicates from the artifacts, and the intranet's page template.</summary>
    public static async Task DedupAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        using var store = new IngestionStore(workspace.Database);
        var live = store.Documents().Where(d => !d.Tombstoned).ToList();
        Console.WriteLine("Exact duplicates (same normalized text, indexed once):");
        foreach (var d in live.Where(d => d.DuplicateOf is not null))
        {
            Console.WriteLine($"  {d.Uri}\n      = {d.DuplicateOf}");
        }

        var detector = new DuplicateDetector();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var d in live.Where(d => d.DuplicateOf is null))
        {
            var document = await DocumentJson.LoadAsync(d.Artifact, cancellationToken);
            detector.AddNear($"{d.DocId}@{d.Version}", d.DocId, document.ToText().Text);
        }

        var pairs = detector.NearDuplicates(0.5);
        Console.WriteLine();
        Console.WriteLine($"Near-duplicates (Jaccard of 5-word shingles at least 0.5; {live.Count(d => d.DuplicateOf is null)} documents in {stopwatch.ElapsedMilliseconds} ms):");
        Console.WriteLine($"  {"Pair",-58}{"MinHash",8}{"Exact",7}{"SimHash",9}");
        foreach (var p in pairs)
        {
            Console.WriteLine($"  {p.A + " ~ " + p.B,-58}{p.EstimatedJaccard,8:F2}{p.Jaccard,7:F2}{p.SimHashDistance,6} bits");
        }

        // The site template: parse every intranet page whole, then keep the blocks that repeat on most pages.
        var full = new HtmlParser(new HtmlParserOptions { MainContentOnly = false });
        var pages = new List<CanonicalDocument>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(workspace.Sources, "intranet"), "*.html", SearchOption.AllDirectories))
        {
            await using var stream = File.OpenRead(file);
            var content = await full.ParseAsync(stream, new ParseContext(Path.GetFileName(file), MediaTypes.Html), cancellationToken);
            pages.Add(new CanonicalDocument(file, "1", null!, new SourceReference("intranet", file), content.Blocks));
        }

        var template = DuplicateDetector.TemplateBlocks(pages);
        var words = pages.Sum(p => p.Blocks.Sum(b => Fingerprints.Normalize(b.ToMarkdown()).Split(' ').Length));
        var templateWords = pages.Sum(p => p.Blocks.Where(b => template.Contains(Fingerprints.Normalize(b.ToMarkdown()))).Sum(b => Fingerprints.Normalize(b.ToMarkdown()).Split(' ').Length));
        Console.WriteLine();
        Console.WriteLine($"Intranet template: {template.Count} blocks repeat on at least half of {pages.Count} pages; they are {100.0 * templateWords / words:F0}% of the words.");
        foreach (var block in template.OrderBy(t => t, StringComparer.Ordinal).Take(6))
        {
            Console.WriteLine($"  \"{(block.Length > 70 ? block[..70] + "…" : block)}\"");
        }
    }

    /// <summary>Retrieval with and without the effective-date filter: what the reader sees on a given day.</summary>
    public static async Task AsOfAsync(Workspace workspace, string question, string? date, string asker, CancellationToken cancellationToken)
    {
        using var store = new IngestionStore(workspace.Database);
        var person = People.Find(asker) ?? throw new ArgumentException($"No person {asker}.");
        DateOnly? asOf = date == "none" ? null : date is null ? ContosoFacts.Today : DateOnly.Parse(date, CultureInfo.InvariantCulture);
        var query = new HashingEmbeddingGenerator().Embed(question);
        var hits = store.Searchable(person.Groups, asOf)
            .Select(h => (h.Chunk, h.Version, h.From, h.To, Score: TensorPrimitives.CosineSimilarity(query, h.Chunk.Embedding)))
            .OrderByDescending(h => h.Score).Take(4).ToList();
        Console.WriteLine($"\"{question}\" as {person.Name}, as of {(asOf is null ? "any date (no filter)" : asOf.Value.ToString("yyyy-MM-dd"))}");
        foreach (var h in hits)
        {
            var window = $"{h.From:yyyy-MM-dd} to {(h.To is null ? "now" : h.To.Value.ToString("yyyy-MM-dd"))}";
            var text = h.Chunk.Text.ReplaceLineEndings(" ");
            Console.WriteLine($"  {h.Score:F3}  {h.Chunk.DocId} v{h.Version} ({window})  {(text.Length > 90 ? text[..90] + "…" : text)}");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// What versioning and deduplication do for retrieval, on golden set v2: the same search over everything the
    /// connectors found, then with the copies set aside, then also as of today. The store decides which versions are
    /// copies and which are superseded; texts come from the generator's truth, so parsing doesn't blur the comparison.
    /// Run it after `reset` and `run`, before `changes`.
    /// </summary>
    public static async Task MeasureAsync(Workspace workspace, string split, CancellationToken cancellationToken)
    {
        using var store = new IngestionStore(workspace.Database);
        var rows = store.Query("SELECT doc_id, version, duplicate_of, effective_to, groups FROM documents WHERE tombstoned = 0",
            r => (Doc: r.GetString(0), Version: r.GetString(1), DuplicateOf: r.IsDBNull(2) ? null : r.GetString(2),
                  To: r.IsDBNull(3) ? (DateOnly?)null : DateOnly.Parse(r.GetString(3), CultureInfo.InvariantCulture), Groups: r.GetString(4)));
        var register = GeneratedCorpus.Register();

        // Every register document as current, copy or superseded, plus attachments the store found to be copies.
        var indexed = new List<(string Key, string Kind, CanonicalDocument Truth, IReadOnlyList<string> Groups)>();
        foreach (var entry in register)
        {
            var row = rows.FirstOrDefault(r => r.Doc == entry.Id && r.Version == entry.Version);
            var truth = await GeneratedCorpus.TruthAsync(entry, cancellationToken);
            var kind = entry.CopyOf is not null || row.DuplicateOf is not null ? "copy" : row.To < ContosoFacts.Today ? "superseded" : "current";
            indexed.Add((kind == "superseded" ? $"{entry.Id}@{entry.Version}" : entry.Id, kind, truth, truth.Metadata.AllowedGroups));
        }

        foreach (var row in rows.Where(r => r.DuplicateOf is not null && register.All(e => e.Id != r.Doc)))
        {
            var original = register.Single(e => $"{e.Id}@{e.Version}" == row.DuplicateOf);
            indexed.Add((row.Doc, "copy", await GeneratedCorpus.TruthAsync(original, cancellationToken), row.Groups.Split(',')));
        }

        var documents = indexed.ToDictionary(d => d.Key, d => new SourceDocument(d.Key, d.Truth.Metadata.Title, d.Truth.Metadata.Tenant, d.Groups, d.Truth.ToText().Text));
        var kinds = indexed.ToDictionary(d => d.Key, d => d.Kind);
        var chunker = new StableChunker(TiktokenTokenizer.CreateForEncoding("o200k_base"));
        var embedder = new HashingEmbeddingGenerator();
        var chunks = indexed.SelectMany(d => chunker.Chunk(d.Truth)
            .Select(c => (Span: new ChunkSpan(d.Key, c.Start, c.End), Vector: embedder.Embed($"{c.Section}\n{c.Text}")))).ToList();

        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        var questions = GoldenSet.Load(path).Where(q => q.Split == split).ToList();
        var harness = new Harness([.. documents.Values], GoldenSet.Sha256(path));
        Console.WriteLine($"Golden set v2, {split}: {questions.Count(q => q.Answerable)} answerable questions; Chapter 6's search over 200-token chunks, k = 4");
        Console.WriteLine();
        Console.WriteLine($"{"Index",-36} {"Docs",4} {"Recall@4",8} {"Suff.",6} {"Copies",7} {"Superseded",10}   Sufficient against everything");
        RunRecord? everything = null;
        foreach (var (name, includes) in new (string, Func<string, bool>)[]
        {
            ("everything the connectors found", _ => true),
            ("copies set aside", kind => kind != "copy"),
            ($"copies set aside, as of {ContosoFacts.Today:yyyy-MM-dd}", kind => kind == "current"),
        })
        {
            var run = await harness.RunAsync(new HashingRetrieval(name, [.. chunks.Where(c => includes(kinds[c.Span.Doc]))], documents), questions, 4, split, cancellationToken);
            everything ??= run;
            int With(string kind) => run.Answerable.Count(q => q.Retrieved.Any(s => kinds[s.Doc] == kind));
            var d = Bootstrap.Paired([.. everything.Answerable.Select(q => q.Sufficient)], [.. run.Answerable.Select(q => q.Sufficient)]);
            Console.WriteLine($"{name,-36} {kinds.Values.Count(includes),4} {run.Mean("Recall"),8:F2} {run.Mean("Sufficient"),6:F2} {With("copy"),7} {With("superseded"),10}   " +
                (run == everything ? "" : $"{d.Mean:+0.00;-0.00} [{d.Low:+0.00;-0.00}, {d.High:+0.00;-0.00}]"));
        }

        Console.WriteLine();
        Console.WriteLine("Copies, Superseded: answerable questions with a copy or a superseded version among their top four results.");
    }

    /// <summary>"Where did this sentence come from?": the chunk, its versions, the file, the parser, the run and the place on the page.</summary>
    public static async Task TraceAsync(Workspace workspace, string sentence, CancellationToken cancellationToken)
    {
        using var store = new IngestionStore(workspace.Database);
        var normalized = Fingerprints.Normalize(sentence);
        var chunks = store.Query("SELECT chunk_id, doc_id, embedding_model, run_id, text FROM chunks", r => (Id: r.GetString(0), Doc: r.GetString(1), Model: r.GetString(2), Run: r.GetString(3), Text: r.GetString(4)))
            .Where(c => Fingerprints.Normalize(c.Text).Contains(normalized, StringComparison.Ordinal)).ToList();
        foreach (var chunk in chunks)
        {
            Console.WriteLine($"chunk     {chunk.Id}");
            Console.WriteLine($"embedding {chunk.Model}, written by {chunk.Run}");
            // A chunk's ID comes from its content, so every document version that contains the same text points at it.
            var uses = store.Query("SELECT doc_id, version, first_block, last_block FROM version_chunks WHERE chunk_id=$c",
                r => (Doc: r.GetString(0), Version: r.GetString(1), First: r.GetInt32(2), Last: r.GetInt32(3)), ("$c", chunk.Id));
            foreach (var (docId, version, first, last) in uses.OrderBy(u => store.Documents(u.Doc).Single(d => d.Version == u.Version).DuplicateOf is not null))
            {
                var row = store.Documents(docId).Single(d => d.Version == version);
                Console.WriteLine($"version   {row.DocId} v{row.Version}, effective {row.EffectiveFrom:yyyy-MM-dd} to {(row.EffectiveTo is null ? "now" : row.EffectiveTo.Value.ToString("yyyy-MM-dd"))}" +
                    $"{(row.Tombstoned ? ", tombstoned" : "")}{(row.DuplicateOf is { } of ? $", copy of {of} (not searched)" : "")}");
                Console.WriteLine($"source    {row.System}: {row.Uri}");
                Console.WriteLine($"file      sha256 {row.Sha256[..16]}…, parsed by {row.Parser} in {row.RunId}");
                var document = await DocumentJson.LoadAsync(row.Artifact, cancellationToken);
                var block = Enumerable.Range(first, last - first + 1).Select(i => document.Blocks[i])
                    .FirstOrDefault(b => Fingerprints.Normalize(b.ToMarkdown()).Contains(normalized, StringComparison.Ordinal));
                if (block?.Anchor is { } anchor)
                {
                    Console.WriteLine($"place     {(anchor.Page is { } p ? $"page {p}" : anchor.Path)}{(anchor.Box is { } b ? $", box {b.Left:F0},{b.Top:F0} to {b.Right:F0},{b.Bottom:F0} pt" : "")}");
                }
                else if (block is not null)
                {
                    Console.WriteLine($"place     block {document.Blocks.ToList().IndexOf(block)} ({block.GetType().Name.Replace("Block", "").ToLowerInvariant()}), section {string.Join(" › ", document.SectionPaths()[document.Blocks.ToList().IndexOf(block)])}");
                }
            }

            Console.WriteLine();
        }

        if (chunks.Count == 0) Console.WriteLine("No chunk contains that sentence.");
    }

    /// <summary>Marks every document a parser version produced as stale, so that the next run parses it again.</summary>
    public static void Backfill(Workspace workspace, string parser)
    {
        using var store = new IngestionStore(workspace.Database);
        var stale = store.Documents().Where(d => d.Parser == parser && !d.Tombstoned).ToList();
        foreach (var d in stale)
        {
            store.Execute("UPDATE source_state SET last_modified=$m, etag=NULL, sha256='' WHERE system=$s AND uri=$u",
                ("$m", DateTimeOffset.MinValue.ToString("O")), ("$s", d.System), ("$u", d.Uri.Split('#')[0]));
        }

        if (stale.Any(d => d.System == "sharepoint")) store.Execute("DELETE FROM checkpoints WHERE system='sharepoint'");
        Console.WriteLine($"{stale.Count} document versions parsed by {parser} will be parsed again on the next run.");
    }

    public static void Quarantine(Workspace workspace)
    {
        using var store = new IngestionStore(workspace.Database);
        foreach (var (system, uri, reason, detail, attempts) in store.Quarantined())
        {
            Console.WriteLine($"{reason,-12} {system}: {uri}\n             {detail} (seen {attempts}x)");
        }
    }

    /// <summary>The source inventory: what each system holds, who owns it and how fresh the index has to be.</summary>
    public static void Inventory(Workspace workspace)
    {
        var register = workspace.Register().Values.ToList();
        Console.WriteLine($"{"Source",-11}{"Files",6}{"MB",7}  {"Formats",-34}{"Freshness",10}{"Permissions",13}  Owner");
        foreach (var contract in DataContract.Contoso)
        {
            var files = register.Where(e => e.System == contract.System).ToList();
            var size = files.Sum(e => new FileInfo(Path.Combine(workspace.Sources, e.Path)) is { Exists: true } f ? f.Length : 0);
            var formats = string.Join(", ", files.GroupBy(e => Path.GetExtension(e.Path).TrimStart('.')).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
            Console.WriteLine($"{contract.System,-11}{files.Count,6}{size / 1024.0 / 1024.0,7:F1}  {formats,-34}{contract.Freshness.TotalHours,8:F0} h{contract.PermissionFreshness.TotalMinutes,10:F0} min  {contract.Owner}");
        }
    }
}

/// <summary>Retries transient HTTP failures (5xx, connection errors) with exponential backoff and jitter, using Polly.</summary>
public sealed class RetryHandler : DelegatingHandler
{
    private static readonly ResiliencePipeline<HttpResponseMessage> Pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
        .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(200),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>().Handle<HttpRequestException>().HandleResult(r => (int)r.StatusCode >= 500),
        })
        .Build();

    public static int Retries;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = 0;
        return await Pipeline.ExecuteAsync(async ct =>
        {
            if (attempt++ > 0) Interlocked.Increment(ref Retries);
            return await base.SendAsync(Clone(request), ct);
        }, cancellationToken);
    }

    // A request can be sent only once, so each attempt sends a copy.
    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return copy;
    }
}
