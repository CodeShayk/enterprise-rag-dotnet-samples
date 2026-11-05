using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ch06.Ingestion.Worker.Dedup;
using Ch06.Ingestion.Worker.Sources;
using Ch06.Ingestion.Worker.Store;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Parsing;
using Microsoft.Extensions.AI;
using Polly;
using Polly.Retry;

namespace Ch06.Ingestion.Worker.Pipeline;

public sealed record IngestionOptions
{
    public int ChannelCapacity { get; init; } = 8;

    public int FetchParallelism { get; init; } = 4;

    public int ParseParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

    public int EmbedBatch { get; init; } = 32;
}

/// <summary>Per-stage counters for the run report: items handled, time spent and the deepest the stage's input queue got.</summary>
public sealed class StageStats(string name)
{
    private long _items, _busyTicks;
    private int _maxQueue;

    public string Name => name;

    public long Items => _items;

    public TimeSpan Busy => TimeSpan.FromTicks(_busyTicks);

    public int MaxQueue => _maxQueue;

    public void Record(TimeSpan busy)
    {
        Interlocked.Increment(ref _items);
        Interlocked.Add(ref _busyTicks, busy.Ticks);
    }

    public void Queue(int depth)
    {
        int current;
        while (depth > (current = _maxQueue) && Interlocked.CompareExchange(ref _maxQueue, depth, current) != current) { }
    }
}

/// <summary>
/// discover → fetch → parse → normalize → chunk → embed → index, each stage a set of workers reading from a bounded
/// channel. When a slow stage's queue fills, the stages before it wait: back-pressure, so a burst of large files can't
/// fill memory. CPU-heavy parsing runs on several workers; the index has one writer, so its writes stay transactional.
/// </summary>
public sealed class IngestionPipeline(
    IngestionStore store, ParserRegistry parsers, StableChunker chunker, IEmbeddingGenerator<string, Embedding<float>> embedder,
    string embeddingModel, string artifacts, LineageEvents lineage, IngestionOptions? options = null)
{
    private readonly IngestionOptions _options = options ?? new IngestionOptions();

    // Transient failures (a share that hiccups, a server that returns 503) are retried with backoff; parse errors aren't.
    private static readonly ResiliencePipeline Retry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(200),
            ShouldHandle = new PredicateBuilder().Handle<IOException>().Handle<HttpRequestException>(),
        })
        .AddTimeout(TimeSpan.FromSeconds(30))
        .Build();

    private readonly HashSet<string> _embeddedThisRun = [];

    public DiscoveryStats Outcomes { get; } = new();

    public List<StageStats> Stages { get; } = [new("fetch"), new("parse"), new("normalize"), new("chunk"), new("embed"), new("index")];

    public List<NearDuplicate> NearDuplicates { get; private set; } = [];

    public async Task<string> RunAsync(IReadOnlyList<IConnector> connectors, CancellationToken cancellationToken)
    {
        // OpenLineage run IDs are UUIDs; version 7 sorts by time.
        var runId = Guid.CreateVersion7().ToString();
        var started = DateTimeOffset.UtcNow;
        lineage.Emit("START", runId, "ingest", connectors.Select(c => Datasets.Input(c.System)), Datasets.Outputs);

        var discovered = Bounded<(IConnector, SourceItem)>();
        var fetched = Bounded<Fetched>();
        var parsed = Bounded<Parsed>();
        var normalized = Bounded<Normalized>();
        var chunked = Bounded<Chunked>();
        var embedded = Bounded<Chunked>();

        var detector = Seed();
        var stopwatch = Stopwatch.StartNew();
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = failed.Token;
        Task[] stages =
        [
            Produce(discovered.Writer, async writer =>
            {
                foreach (var connector in connectors)
                {
                    await foreach (var item in connector.DiscoverAsync(store, Outcomes, cancellationToken))
                    {
                        if (item.Deleted) Delete(item, runId);
                        else await writer.WriteAsync((connector, item), cancellationToken);
                    }
                }
            }),
            Stage(discovered.Reader, fetched.Writer, Stages[0], _options.FetchParallelism, Fetch, cancellationToken),
            Stage(fetched.Reader, parsed.Writer, Stages[1], _options.ParseParallelism, Parse, cancellationToken),
            Stage(parsed.Reader, normalized.Writer, Stages[2], 1, (p, ct) => Normalize(p, detector, runId, ct), cancellationToken),
            Stage(normalized.Reader, chunked.Writer, Stages[3], 2, Chunk, cancellationToken),
            Stage(chunked.Reader, embedded.Writer, Stages[4], 1, Embed, cancellationToken),
            Stage<Chunked, bool>(embedded.Reader, null, Stages[5], 1, (c, _) => Task.FromResult<IEnumerable<bool>>([Index(c, runId)]), cancellationToken),
        ];
        await Task.WhenAll(stages.Select(stage => Guard(failed, stage)));

        foreach (var duplicate in store.ResolveDuplicates().Where(d => d.RunId == runId))
        {
            Outcomes.Add(duplicate.System, "exact duplicate");
        }

        foreach (var connector in connectors) connector.Commit(store);
        NearDuplicates = [.. detector.NearDuplicates().Where(p => p.Jaccard < 1)];
        var (chunks, embeddedCount) = store.ChunkCounts();
        lineage.Emit("COMPLETE", runId, "ingest", connectors.Select(c => Datasets.Input(c.System)), Datasets.Outputs,
            new JsonObject { ["contoso_counts"] = new JsonObject { ["versionChunks"] = chunks, ["embeddedChunks"] = embeddedCount } });
        store.SaveRun(runId, started, DateTimeOffset.UtcNow, "COMPLETE", $"{stopwatch.Elapsed.TotalSeconds:F1} s");
        return runId;
    }

    /// <summary>If a stage fails, cancel the others at once: a stage blocked writing to a full channel would otherwise wait forever.</summary>
    private static async Task Guard(CancellationTokenSource failed, Task stage)
    {
        try
        {
            await stage;
        }
        catch
        {
            await failed.CancelAsync();
            throw;
        }
    }

    private Channel<T> Bounded<T>() =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(_options.ChannelCapacity) { FullMode = BoundedChannelFullMode.Wait });

    private static async Task Produce<T>(ChannelWriter<T> writer, Func<ChannelWriter<T>, Task> produce)
    {
        try
        {
            await produce(writer);
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>Runs <paramref name="workers"/> consumers of one channel; completes the next channel when all are done.</summary>
    private static async Task Stage<TIn, TOut>(ChannelReader<TIn> input, ChannelWriter<TOut>? output, StageStats stats, int workers,
        Func<TIn, CancellationToken, Task<IEnumerable<TOut>>> work, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                await foreach (var item in input.ReadAllAsync(cancellationToken))
                {
                    stats.Queue(input.Count + 1);
                    var started = Stopwatch.GetTimestamp();
                    var results = await work(item, cancellationToken);
                    stats.Record(Stopwatch.GetElapsedTime(started));
                    if (output is null) continue;
                    foreach (var result in results) await output.WriteAsync(result, cancellationToken);
                }
            }, cancellationToken)));
        }
        finally
        {
            output?.Complete();
        }
    }

    // ---- fetch: bytes and their hash; a file whose bytes didn't change is only "touched" ----

    private async Task<IEnumerable<Fetched>> Fetch((IConnector Connector, SourceItem Item) work, CancellationToken cancellationToken)
    {
        var (connector, item) = work;
        var bytes = item.Content ?? await Retry.ExecuteAsync(async ct => await connector.FetchAsync(item, ct), cancellationToken);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var previous = store.State(item.System, item.Uri);
        if (previous is not null && previous.Sha256 == sha)
        {
            // New timestamp or ETag, same bytes: remember the new signal so the cheap check succeeds next time.
            store.SaveState(previous with { LastModified = item.LastModified, Length = item.Length, ETag = item.ETag ?? previous.ETag });
            Outcomes.Add(item.System, "touched");
            return [];
        }

        Outcomes.Add(item.System, previous is null ? "added" : "modified");
        return [new Fetched(item, bytes, sha, previous)];
    }

    // ---- parse: unreadable files are quarantined; scans wait for OCR; attachments are parsed in turn ----

    private async Task<IEnumerable<Parsed>> Parse(Fetched fetched, CancellationToken cancellationToken)
    {
        var results = new List<Parsed>();
        await ParseInto(fetched, depth: 0, results, cancellationToken);
        return results;
    }

    private async Task ParseInto(Fetched fetched, int depth, List<Parsed> results, CancellationToken cancellationToken)
    {
        var item = fetched.Item;
        var context = new ParseContext(Path.GetFileName(item.Uri), item.MediaType);
        ParsedContent content;
        try
        {
            content = await parsers.ParseAsync(new MemoryStream(fetched.Bytes), context, cancellationToken);
        }
        catch (Exception e) when (e is UnsupportedFormatException or ParserLimitException or DocumentParseException)
        {
            // A parse error is deterministic: retrying the same bytes fails the same way. Quarantine and move on;
            // the item is tried again when its bytes change.
            var attempts = store.Quarantine(item.System, item.Uri, "parse-error", e.Message);
            store.SaveState(new SourceState(item.System, item.Uri, item.ETag, item.LastModified, item.Length, fetched.Sha256, null, null, null));
            Outcomes.Add(item.System, attempts > 1 ? "quarantined (again)" : "quarantined");
            return;
        }

        if (content.Blocks.Count == 0 && content.Pages.Count > 0 && content.Pages.All(p => !p.HasTextLayer))
        {
            store.Quarantine(item.System, item.Uri, "needs-ocr", $"{content.Pages.Count} scanned pages without a text layer (Chapter 8)");
            store.SaveState(new SourceState(item.System, item.Uri, item.ETag, item.LastModified, item.Length, fetched.Sha256, null, null, null));
            Outcomes.Add(item.System, "needs OCR");
            return;
        }

        results.Add(new Parsed(fetched, content, parsers.For(item.MediaType)!));
        if (depth >= context.Limits.MaxAttachmentDepth) return;
        foreach (var attachment in content.Attachments)
        {
            var child = new SourceItem(item.System, $"{item.Uri}#{attachment.FileName}", attachment.MediaType, item.LastModified, attachment.Content.Length)
            {
                Parent = item.Uri,
                Register = item.Register,
                Content = attachment.Content,
            };
            Outcomes.Add(item.System, "attachments");
            await ParseInto(new Fetched(child, attachment.Content, Convert.ToHexStringLower(SHA256.HashData(attachment.Content)), null), depth + 1, results, cancellationToken);
        }
    }

    // ---- normalize: contract, metadata, canonical document, exact duplicates ----

    private async Task<IEnumerable<Normalized>> Normalize(Parsed parsed, DuplicateDetector detector, string runId, CancellationToken cancellationToken)
    {
        var item = parsed.Fetched.Item;
        var register = item.Register;
        if (register is null)
        {
            store.Quarantine(item.System, item.Uri, "contract", "not in the source's register: no metadata");
            Outcomes.Add(item.System, "contract violation");
            return [];
        }

        var (metadata, problems) = DataContract.Validate(register.Metadata);
        if (metadata is null)
        {
            store.Quarantine(item.System, item.Uri, "contract", string.Join("; ", problems));
            store.SaveState(new SourceState(item.System, item.Uri, item.ETag, item.LastModified, item.Length, parsed.Fetched.Sha256, null, null, null));
            Outcomes.Add(item.System, "contract violation");
            return [];
        }

        // An attachment is its own document, as sensitive as the message it came with.
        var id = item.Parent is null ? register.Id : $"{register.Id}/{Path.GetFileName(item.Uri.Split('#')[1])}";
        var title = item.Parent is null ? metadata.Title : $"{item.Uri.Split('#')[1]} (attachment to {metadata.Title})";
        var document = new CanonicalDocument(id, register.Version, metadata with { Title = title },
            new SourceReference(item.System, item.Uri)
            {
                MediaType = item.MediaType, ETag = item.ETag, LastModified = item.LastModified, Length = item.Length, ContentHash = parsed.Fetched.Sha256,
            },
            parsed.Content.Blocks)
        {
            Pages = parsed.Content.Pages,
            Parser = $"{parsed.Parser.Name}/{parsed.Parser.Version}",
        };

        var text = document.ToText();
        var textHash = Fingerprints.TextHash(text.Text);
        var previous = parsed.Fetched.Previous;
        if (previous is not null && previous.TextHash == textHash && previous.Version == register.Version && item.Parent is null)
        {
            // New bytes, same text: a re-save, or a backfill whose parser produced the same text. Remember the new
            // bytes and the parser that read them; nothing downstream needs to run.
            store.SaveState(previous with { LastModified = item.LastModified, Length = item.Length, ETag = item.ETag, Sha256 = parsed.Fetched.Sha256 });
            store.Execute("UPDATE documents SET parser=$p, sha256=$h WHERE doc_id=$d AND version=$v",
                ("$p", document.Parser), ("$h", parsed.Fetched.Sha256), ("$d", previous.DocId), ("$v", previous.Version));
            Outcomes.Add(item.System, "same text");
            return [];
        }

        // Exact duplicates are decided after the run, when every copy has arrived (parallel parsing reorders them).
        // Their chunks share content-addressed IDs, so a copy costs no extra embeddings.
        var key = $"{id}@{register.Version}";
        detector.AddNear(key, id, text.Text);

        var artifact = Path.Combine(artifacts, $"{Safe(id)}@{Safe(register.Version)}.json");
        await DocumentJson.SaveAsync(document, artifact, cancellationToken);
        store.Release(item.System, item.Uri);
        return [new Normalized(parsed, document, textHash, null, artifact)];
    }

    // ---- chunk and embed: only chunks the store hasn't seen get an embedding ----

    private Task<IEnumerable<Chunked>> Chunk(Normalized normalized, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<Chunked>>([new Chunked(normalized, chunker.Chunk(normalized.Document), new Dictionary<string, float[]>())]);

    private async Task<IEnumerable<Chunked>> Embed(Chunked chunked, CancellationToken cancellationToken)
    {
        // One embed worker, so a set of this run's IDs catches copies that haven't reached the index yet.
        var fresh = chunked.Chunks.Where(c => !store.HasChunk(c.ChunkId) && _embeddedThisRun.Add(c.ChunkId)).DistinctBy(c => c.ChunkId).ToList();
        var vectors = new Dictionary<string, float[]>();
        foreach (var batch in fresh.Chunk(_options.EmbedBatch))
        {
            var embeddings = await embedder.GenerateAsync(batch.Select(c => $"{c.Section}\n{c.Text}"), cancellationToken: cancellationToken);
            for (var i = 0; i < batch.Length; i++) vectors[batch[i].ChunkId] = embeddings[i].Vector.ToArray();
        }

        Outcomes.Add(chunked.Normalized.Document.Source.System, "chunks embedded", vectors.Count);
        Outcomes.Add(chunked.Normalized.Document.Source.System, "chunks reused", chunked.Chunks.Count - vectors.Count);
        return [chunked with { Vectors = vectors }];
    }

    // ---- index: one transaction per document version ----

    private bool Index(Chunked chunked, string runId)
    {
        var (document, item) = (chunked.Normalized.Document, chunked.Normalized.Parsed.Fetched.Item);
        store.InTransaction(() => WriteVersion(chunked, document, item, runId));
        return true;
    }

    private void WriteVersion(Chunked chunked, CanonicalDocument document, SourceItem item, string runId)
    {
        foreach (var chunk in chunked.Chunks.Where(c => chunked.Vectors.ContainsKey(c.ChunkId)))
        {
            store.AddChunk(chunk.ChunkId, document.Id, chunk.Text, chunk.Section, chunked.Vectors[chunk.ChunkId], embeddingModel, runId);
        }

        store.SetVersionChunks(document.Id, document.Version, [.. chunked.Chunks.Select(c => (c.ChunkId, c.Start, c.End, c.FirstBlock, c.LastBlock))]);
        var m = document.Metadata;
        store.UpsertDocument(new DocumentRow(document.Id, document.Version, item.System, item.Uri, m.Title, m.Tenant, m.DocumentType, m.Sensitivity,
            m.AllowedGroups, m.EffectiveFrom, m.EffectiveTo, document.Source.ContentHash!, chunked.Normalized.TextHash, document.Parser!,
            chunked.Normalized.Artifact, chunked.Normalized.DuplicateOf, runId));
        store.LinkVersions(document.Id);
        store.SaveState(new SourceState(item.System, item.Uri, item.ETag, item.LastModified, item.Length, document.Source.ContentHash!,
            chunked.Normalized.TextHash, document.Id, document.Version));
    }

    // ---- deletes: a tombstone keeps the record, removes the content from search ----

    private void Delete(SourceItem item, string runId)
    {
        var state = store.State(item.System, item.Uri);
        if (state?.DocId is { } docId && state.Version is { } version)
        {
            store.Tombstone(docId, version, runId);
            store.LinkVersions(docId);
            lineage.Emit("COMPLETE", runId, "tombstone", [(Datasets.Input(item.System).Namespace, item.Uri)], [(Datasets.Store, $"documents/{docId}@{version}")]);
        }

        store.ForgetState(item.System, item.Uri);
        Outcomes.Add(item.System, "deleted");
    }

    private DuplicateDetector Seed()
    {
        var detector = new DuplicateDetector();
        foreach (var row in store.Documents().Where(d => !d.Tombstoned && d.DuplicateOf is null))
        {
            detector.AddExact($"{row.DocId}@{row.Version}", row.TextHash);
        }

        return detector;
    }

    private static string Safe(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c));

    private sealed record Fetched(SourceItem Item, byte[] Bytes, string Sha256, SourceState? Previous);

    private sealed record Parsed(Fetched Fetched, ParsedContent Content, IDocumentParser Parser);

    private sealed record Normalized(Parsed Parsed, CanonicalDocument Document, string TextHash, string? DuplicateOf, string Artifact);

    private sealed record Chunked(Normalized Normalized, IReadOnlyList<ChunkDraft> Chunks, IReadOnlyDictionary<string, float[]> Vectors);
}
