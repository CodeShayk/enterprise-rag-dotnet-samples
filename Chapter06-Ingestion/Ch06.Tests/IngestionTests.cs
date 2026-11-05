using System.Text.Json;
using Ch06.Ingestion.Worker;
using Ch06.Ingestion.Worker.Dedup;
using Ch06.Ingestion.Worker.Pipeline;
using Ch06.Ingestion.Worker.Sources;
using Ch06.Ingestion.Worker.Store;
using Contoso.AI;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Parsing;
using Microsoft.ML.Tokenizers;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ch06.Tests;

public sealed class FingerprintTests
{
    private const string Bulletin = "Apply lockout/tagout (LOTO) to the motor and isolate the seal-water and process lines before opening any CP-200 seal housing. Verify zero energy before work starts. Zero energy means that the motor can't start from the panel, the local pressure gauges read zero and the casing drain is open. Each person working on the pump fits their own lock.";

    [Fact]
    public void Normalization_ignores_formatting()
    {
        Assert.Equal(Fingerprints.TextHash("Verify  zero energy.\nNow!"), Fingerprints.TextHash("verify zero ENERGY now"));
    }

    [Fact]
    public void MinHash_estimates_the_jaccard_similarity_of_a_regional_variant()
    {
        var a = Fingerprints.Shingles(Bulletin + " At the Riverside plant, report problems to the maintenance planner on extension 4410.");
        var b = Fingerprints.Shingles(Bulletin + " At the Lakeside plant, report problems to the shift supervisor on extension 6102.");
        var exact = Fingerprints.Jaccard(a, b);
        var estimate = Fingerprints.EstimatedJaccard(Fingerprints.MinHash(a), Fingerprints.MinHash(b));
        Assert.InRange(exact, 0.6, 0.9);
        Assert.InRange(estimate, exact - 0.12, exact + 0.12);
        Assert.True(Fingerprints.Hamming(Fingerprints.SimHash(a), Fingerprints.SimHash(b)) <= 12);
    }

    [Fact]
    public void Unrelated_documents_are_far_apart()
    {
        var a = Fingerprints.Shingles(Bulletin);
        var b = Fingerprints.Shingles("The HG-400 is filled at the factory with 18 liters of ISO VG 220 mineral gear oil. Change the oil after the first 500 operating hours.");
        Assert.True(Fingerprints.EstimatedJaccard(Fingerprints.MinHash(a), Fingerprints.MinHash(b)) < 0.1);
        Assert.True(Fingerprints.Hamming(Fingerprints.SimHash(a), Fingerprints.SimHash(b)) > 15);
    }
}

public sealed class ChunkAndContractTests
{
    private static readonly StableChunker Chunker = new(TiktokenTokenizer.CreateForEncoding("o200k_base"), maxTokens: 40);

    private static CanonicalDocument Doc(params Block[] blocks) =>
        new("SOP-T", "1", null!, new SourceReference("test", "t"), blocks);

    [Fact]
    public void Changing_one_section_changes_only_its_chunk_ids()
    {
        Block[] before = [new HeadingBlock(1, "Steps"), new ParagraphBlock("Drain the casing and remove the coupling guard."),
            new HeadingBlock(1, "Torque"), new ParagraphBlock("Tighten the seal gland nuts evenly to 22 N·m.")];
        Block[] after = [before[0], before[1], before[2], new ParagraphBlock("Tighten the seal gland nuts evenly to 25 N·m.")];
        var a = Chunker.Chunk(Doc(before)).Select(c => c.ChunkId).ToList();
        var b = Chunker.Chunk(Doc(after)).Select(c => c.ChunkId).ToList();
        Assert.Equal(2, a.Count);
        Assert.Equal(a[0], b[0]);
        Assert.NotEqual(a[1], b[1]);
    }

    [Fact]
    public void The_contract_names_every_missing_field()
    {
        var reported = JsonSerializer.SerializeToElement(new { title = "Draft", tenant = "service", documentType = "service-report", allowedGroups = new[] { "service" } });
        var (metadata, problems) = DataContract.Validate(reported);
        Assert.Null(metadata);
        Assert.Equal(["missing sensitivity", "missing owner"], problems);
    }
}

public sealed class PipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ch06-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_second_run_skips_unchanged_files_and_a_deletion_leaves_a_tombstone()
    {
        Write("fileshare/a.md", "# A\n\nSeal water must exceed suction pressure by 1 bar.", "DOC-A", "1", "2025-01-01");
        Write("fileshare/b.md", "# B\n\nReplace the elastomer element when its teeth crack.", "DOC-B", "1", "2025-01-01");
        Assert.Equal(2, (await Run()).Get("fileshare", "added"));

        var second = await Run();
        Assert.Equal(2, second.Get("fileshare", "unchanged"));
        Assert.Equal(0, second.Get("fileshare", "modified"));

        File.Delete(Path.Combine(_root, "fileshare/b.md"));
        var third = await Run();
        Assert.Equal(1, third.Get("fileshare", "deleted"));
        using var store = new IngestionStore(Path.Combine(_root, "test.db"));
        Assert.True(store.Documents("DOC-B").Single().Tombstoned);
        Assert.DoesNotContain(store.Searchable(new HashSet<string> { "fluid" }, null), c => c.Chunk.DocId == "DOC-B");
    }

    [Fact]
    public async Task A_new_version_closes_the_previous_window()
    {
        Write("fileshare/sop-r2.md", "# SOP\n\nTighten the gland nuts to 22 N·m.", "SOP-X", "2", "2024-03-04");
        Write("fileshare/sop-r3.md", "# SOP\n\nTighten the gland nuts to 25 N·m.", "SOP-X", "3", "2026-09-14");
        await Run();
        using var store = new IngestionStore(Path.Combine(_root, "test.db"));
        var versions = store.Documents("SOP-X");
        Assert.Equal(new DateOnly(2026, 9, 13), versions.Single(v => v.Version == "2").EffectiveTo);
        Assert.Equal("3", versions.Single(v => v.Version == "2").SupersededBy);
        var groups = new HashSet<string> { "fluid" };
        Assert.All(store.Searchable(groups, new DateOnly(2025, 6, 1)).Where(c => c.Chunk.DocId == "SOP-X"), c => Assert.Equal("2", c.Version));
        Assert.All(store.Searchable(groups, new DateOnly(2027, 2, 1)).Where(c => c.Chunk.DocId == "SOP-X"), c => Assert.Equal("3", c.Version));
    }

    private async Task<DiscoveryStats> Run()
    {
        using var store = new IngestionStore(Path.Combine(_root, "test.db"));
        var register = JsonSerializer.Deserialize<List<RegisterEntry>>(File.ReadAllText(Path.Combine(_root, "manifest.json")), DocumentJson.Options)!
            .ToDictionary(e => e.Path);
        var pipeline = new IngestionPipeline(store, ParserRegistry.Default, new StableChunker(TiktokenTokenizer.CreateForEncoding("o200k_base")),
            new HashingEmbeddingGenerator(), "hashing-384", Path.Combine(_root, "artifacts"), new LineageEvents(_root, store));
        await pipeline.RunAsync([new FileShareConnector(_root, "fileshare", register)], TestContext.Current.CancellationToken);
        return pipeline.Outcomes;
    }

    private void Write(string path, string markdown, string id, string version, string from)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, markdown);
        var manifest = Path.Combine(_root, "manifest.json");
        var entries = File.Exists(manifest) ? JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(manifest))! : [];
        entries.Add(JsonSerializer.SerializeToElement(new
        {
            path, system = "fileshare", id, version, mediaType = MediaTypes.Markdown, lastModified = DateTimeOffset.UtcNow, length = 0, sha256 = "", truth = "",
            metadata = new { title = id, tenant = "fluid", documentType = "sop", sensitivity = "internal", allowedGroups = new[] { "fluid" }, owner = "Test", effectiveFrom = from },
        }));
        File.WriteAllText(manifest, JsonSerializer.Serialize(entries));
    }
}

public sealed class GraphDeltaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ch06-graph-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task The_delta_link_returns_only_changes_and_deletions()
    {
        var library = Path.Combine(_root, "sharepoint", "hr");
        Directory.CreateDirectory(library);
        for (var i = 0; i < 7; i++) File.WriteAllText(Path.Combine(library, $"policy-{i}.md"), $"# Policy {i}");
        using var http = new HttpClient(new FakeGraphHandler(_root, "drive")) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        using var store = new IngestionStore(Path.Combine(_root, "test.db"));
        var connector = new GraphDeltaConnector(http, "drive", new Dictionary<string, RegisterEntry>());

        var first = await Collect(connector, store);
        Assert.Equal(7, first.Count);   // two pages: five items, then two
        connector.Commit(store);

        File.WriteAllText(Path.Combine(library, "policy-2.md"), "# Policy 2, revised");
        File.SetLastWriteTimeUtc(Path.Combine(library, "policy-2.md"), DateTime.UtcNow.AddMinutes(5));
        File.Delete(Path.Combine(library, "policy-5.md"));
        var second = await Collect(connector, store);
        Assert.Equal(["sharepoint/hr/policy-2.md"], second.Where(i => !i.Deleted).Select(i => i.Uri));
        Assert.Equal(["sharepoint/hr/policy-5.md"], second.Where(i => i.Deleted).Select(i => i.Uri));
    }

    private static async Task<List<SourceItem>> Collect(GraphDeltaConnector connector, IngestionStore store)
    {
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync(store, new DiscoveryStats(), TestContext.Current.CancellationToken)) items.Add(item);
        return items;
    }
}

public sealed class PostgresLineageTests
{
    [Fact]
    public async Task Lineage_events_can_be_queried_by_input_dataset()
    {
        // Runs only where Docker runs Linux containers; it's skipped otherwise.
        Assert.SkipUnless(DockerAvailable(), "Docker isn't running Linux containers, so the PostgreSQL container can't start.");
        await using var container = new PostgreSqlBuilder("postgres:17").Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        await using var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        var store = new PostgresLineageStore(dataSource);
        await store.CreateAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.SerializeToElement(new { inputs = new[] { new { @namespace = "fileshare", name = "fileshare/a.pdf" } } });
        await store.AddEventAsync("run-1", "COMPLETE", "ingest", json, TestContext.Current.CancellationToken);
        Assert.Equal(["run-1"], await store.RunsReadingAsync("fileshare/a.pdf", TestContext.Current.CancellationToken));
    }

    private static bool DockerAvailable()
    {
        try
        {
            // An engine that runs only Windows containers (a GitHub Windows runner) can't start the postgres image,
            // and a daemon that doesn't answer in ten seconds counts as unavailable.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", "info --format {{.OSType}}") { RedirectStandardOutput = true, RedirectStandardError = true });
            var output = process!.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            Task.WaitAll(output, errors);
            return process.ExitCode == 0 && output.Result.Trim() == "linux";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
