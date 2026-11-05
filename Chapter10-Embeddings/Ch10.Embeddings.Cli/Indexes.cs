using System.Diagnostics;
using Contoso.Knowledge.Embeddings;
using Npgsql;
using NpgsqlTypes;
using PgVector = Pgvector.Vector;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Testcontainers.PostgreSql;
using Testcontainers.Qdrant;
using static Qdrant.Client.Grpc.Conditions;

namespace Ch10.Embeddings.Cli;

/// <summary>
/// Approximate indexes against exact search on the synthetic log: pgvector's HNSW and IVFFlat and Qdrant's HNSW,
/// each swept over its query-time knob, then the same indexes under filters of falling selectivity. Both stores run
/// in containers (Testcontainers), so Docker must be running.
/// </summary>
public static class Indexes
{
    public const string PgvectorImage = "pgvector/pgvector:0.8.7-pg17";
    public const string QdrantImage = "qdrant/qdrant:v1.20.0";

    private sealed record FilterCase(string Name, string Sql, Filter Qdrant, Func<LogEntry, bool> Matches);

    public static async Task RunAsync()
    {
        var entries = Synthetic.LoadEntries();
        var vectors = Synthetic.Load("logs.bin");
        var queries = Synthetic.Load("queries.bin");
        var exact = new ExactIndex(vectors);
        var truth = queries.AsParallel().AsOrdered().Select(q => exact.Search(q, 10).Select(r => r.Index).ToHashSet()).ToArray();
        Console.WriteLine($"{vectors.Length:N0} vectors, {queries.Length} queries, one client, latency measured in the client; {PgvectorImage}, {QdrantImage}");

        // A parallel HNSW build shares memory through /dev/shm, which Docker caps at 64 MB unless asked for more.
        await using var postgres = new PostgreSqlBuilder(PgvectorImage).WithCreateParameterModifier(p => p.HostConfig!.ShmSize = 1L << 30).Build();
        await using var qdrantContainer = new QdrantBuilder(QdrantImage).Build();
        await Task.WhenAll(postgres.StartAsync(), qdrantContainer.StartAsync());

        // ---- pgvector ----
        var builder = new NpgsqlDataSourceBuilder(postgres.GetConnectionString());
        builder.UseVector();
        await using var dataSource = builder.Build();
        await using (var setup = dataSource.CreateConnection())
        {
            await setup.OpenAsync();
            await Execute(setup, "CREATE EXTENSION IF NOT EXISTS vector");
            await setup.ReloadTypesAsync();
            await Execute(setup, "CREATE TABLE logs (id int PRIMARY KEY, site text, tag text, year int, embedding vector(384))");
            await using (var writer = await setup.BeginBinaryImportAsync("COPY logs (id, site, tag, year, embedding) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var e in entries)
                {
                    await writer.StartRowAsync();
                    await writer.WriteAsync(e.Id, NpgsqlDbType.Integer);
                    await writer.WriteAsync(e.Site, NpgsqlDbType.Text);
                    await writer.WriteAsync(e.Tag, NpgsqlDbType.Text);
                    await writer.WriteAsync(e.Year, NpgsqlDbType.Integer);
                    await writer.WriteAsync(new PgVector(vectors[e.Id]));
                }

                await writer.CompleteAsync();
            }

            await Execute(setup, "CREATE INDEX ON logs (site); CREATE INDEX ON logs (tag); CREATE INDEX ON logs (year); ANALYZE logs");
        }

        await using var connection = dataSource.CreateConnection();
        await connection.OpenAsync();
        await Execute(connection, "SET maintenance_work_mem = '1GB'");

        Console.WriteLine();
        Console.WriteLine($"{"Index and setting",-40} {"Recall@10",9} {"p50 ms",7} {"p95 ms",7}");
        await Execute(connection, "SET enable_indexscan = off");
        Report("pgvector, no index (exact scan)", await Sweep(i => PgSearchAsync(connection, queries[i], "")));
        await Execute(connection, "SET enable_indexscan = on");

        var built = Stopwatch.StartNew();
        await Execute(connection, "CREATE INDEX logs_hnsw ON logs USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64)");
        var hnswBuild = built.Elapsed;
        var hnswSize = await Scalar<long>(connection, "SELECT pg_relation_size('logs_hnsw')");
        foreach (var ef in new[] { 10, 20, 40, 80, 160 })
        {
            await Execute(connection, $"SET hnsw.ef_search = {ef}");
            Report($"pgvector HNSW, ef_search {ef}", await Sweep(i => PgSearchAsync(connection, queries[i], "")));
        }

        // ---- filtered search on pgvector's HNSW: without and with iterative index scans (pgvector 0.8) ----
        var filters = new FilterCase[]
        {
            new("year 2026", "WHERE year = 2026", new Filter { Must = { Match("year", 2026) } }, e => e.Year == 2026),
            new("site Lakeside", "WHERE site = 'Lakeside'", new Filter { Must = { MatchKeyword("site", "Lakeside") } }, e => e.Site == "Lakeside"),
            new("site Lakeside, year 2026", "WHERE site = 'Lakeside' AND year = 2026",
                new Filter { Must = { MatchKeyword("site", "Lakeside"), Match("year", 2026) } }, e => e.Site == "Lakeside" && e.Year == 2026),
            new("tag P-201", "WHERE tag = 'P-201'", new Filter { Must = { MatchKeyword("tag", "P-201") } }, e => e.Tag == "P-201"),
        };
        var filteredTruth = filters.ToDictionary(f => f.Name, f => queries.AsParallel().AsOrdered()
            .Select(q => exact.Search(q, 10, i => f.Matches(entries[i])).Select(r => r.Index).ToHashSet()).ToArray());
        var filteredRows = new List<string>();
        await Execute(connection, "SET hnsw.ef_search = 40");
        foreach (var f in filters)
        {
            var share = entries.Count(f.Matches) / (double)entries.Count;
            await Execute(connection, "SET hnsw.iterative_scan = off");
            var (recallOff, returnedOff) = await FilteredSweep(i => PgSearchAsync(connection, queries[i], f.Sql), filteredTruth[f.Name]);
            await Execute(connection, "SET hnsw.iterative_scan = relaxed_order");
            var (recallOn, returnedOn) = await FilteredSweep(i => PgSearchAsync(connection, queries[i], f.Sql), filteredTruth[f.Name]);
            filteredRows.Add($"{f.Name,-26} {share,8:P1} {recallOff,10:F3} {returnedOff,9:F1} {recallOn,10:F3} {returnedOn,9:F1}");
        }

        // The same graph built with more care: ef_construction is the build-time breadth, pgvector's default is 64.
        await Execute(connection, "SET hnsw.iterative_scan = off; DROP INDEX logs_hnsw");
        built.Restart();
        await Execute(connection, "CREATE INDEX logs_hnsw ON logs USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 200)");
        var carefulBuild = built.Elapsed;
        foreach (var ef in new[] { 40, 160 })
        {
            await Execute(connection, $"SET hnsw.ef_search = {ef}");
            Report($"pgvector HNSW, ef_construction 200, ef {ef}", await Sweep(i => PgSearchAsync(connection, queries[i], "")));
        }

        await Execute(connection, "DROP INDEX logs_hnsw");
        built.Restart();
        await Execute(connection, "CREATE INDEX logs_ivf ON logs USING ivfflat (embedding vector_cosine_ops) WITH (lists = 316)");
        var ivfBuild = built.Elapsed;
        var ivfSize = await Scalar<long>(connection, "SELECT pg_relation_size('logs_ivf')");
        foreach (var probes in new[] { 1, 4, 16, 32 })
        {
            await Execute(connection, $"SET ivfflat.probes = {probes}");
            Report($"pgvector IVFFlat (316 lists), probes {probes}", await Sweep(i => PgSearchAsync(connection, queries[i], "")));
        }

        // ---- Qdrant ----
        var qdrant = new QdrantClient(new Uri(qdrantContainer.GetGrpcConnectionString()));
        await qdrant.CreateCollectionAsync("logs", new VectorParams { Size = 384, Distance = Distance.Cosine }, hnswConfig: new HnswConfigDiff { M = 16, EfConstruct = 100 });
        foreach (var (field, type) in new[] { ("site", PayloadSchemaType.Keyword), ("tag", PayloadSchemaType.Keyword), ("year", PayloadSchemaType.Integer) })
        {
            await qdrant.CreatePayloadIndexAsync("logs", field, type);
        }

        built.Restart();
        foreach (var batch in entries.Chunk(1000))
        {
            await qdrant.UpsertAsync("logs", [.. batch.Select(e => new PointStruct
            {
                Id = (ulong)e.Id,
                Vectors = vectors[e.Id],
                Payload = { ["site"] = e.Site, ["tag"] = e.Tag, ["year"] = e.Year },
            })]);
        }

        var info = await qdrant.GetCollectionInfoAsync("logs");
        while (info.Status != CollectionStatus.Green || info.IndexedVectorsCount < (ulong)entries.Count)
        {
            await Task.Delay(1000);
            info = await qdrant.GetCollectionInfoAsync("logs");
        }

        var qdrantBuild = built.Elapsed;
        Report("Qdrant, exact", await Sweep(i => QdrantSearchAsync(qdrant, queries[i], new SearchParams { Exact = true }, null)));
        foreach (var ef in new[] { 10, 20, 40, 80, 160 })
        {
            Report($"Qdrant HNSW, hnsw_ef {ef}", await Sweep(i => QdrantSearchAsync(qdrant, queries[i], new SearchParams { HnswEf = (ulong)ef }, null)));
        }

        Console.WriteLine();
        Console.WriteLine($"Build: pgvector HNSW (m 16, ef_construction 64) {hnswBuild.TotalSeconds:F0} s, {hnswSize / 1e6:F0} MB, ef_construction 200 {carefulBuild.TotalSeconds:F0} s; " +
            $"IVFFlat {ivfBuild.TotalSeconds:F0} s, {ivfSize / 1e6:F0} MB; Qdrant HNSW (m 16, ef_construct 100) {qdrantBuild.TotalSeconds:F0} s from the first upsert to indexed, {info.SegmentsCount} segments");

        Console.WriteLine();
        Console.WriteLine($"{"Filter (HNSW, ef 40)",-26} {"Matches",8} {"pgvector",10} {"",9} {"iterative",10} {"",9} {"Qdrant",8}");
        Console.WriteLine($"{"",-26} {"",8} {"recall@10",10} {"returned",9} {"recall@10",10} {"returned",9} {"recall@10",8}");
        for (var k = 0; k < filters.Length; k++)
        {
            var f = filters[k];
            var (recallQdrant, _) = await FilteredSweep(i => QdrantSearchAsync(qdrant, queries[i], new SearchParams { HnswEf = 40 }, f.Qdrant), filteredTruth[f.Name]);
            Console.WriteLine($"{filteredRows[k]} {recallQdrant,8:F3}");
        }

        async Task<(double Recall, List<double> Milliseconds)> Sweep(Func<int, Task<List<int>>> search)
        {
            var times = new List<double>(queries.Length);
            var recall = 0.0;
            for (var i = 0; i < 100; i++) await search(i);   // warm-up, so the first setting on a fresh index isn't timed cold
            for (var i = 0; i < queries.Length; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                var ids = await search(i);
                times.Add(stopwatch.Elapsed.TotalMilliseconds);
                recall += ids.Count(truth[i].Contains) / 10.0;
            }

            return (recall / queries.Length, times);
        }

        async Task<(double Recall, double Returned)> FilteredSweep(Func<int, Task<List<int>>> search, HashSet<int>[] expected)
        {
            var (recall, returned) = (0.0, 0.0);
            for (var i = 0; i < queries.Length; i++)
            {
                var ids = await search(i);
                recall += expected[i].Count == 0 ? 1 : ids.Count(expected[i].Contains) / (double)expected[i].Count;
                returned += ids.Count;
            }

            return (recall / queries.Length, returned / queries.Length);
        }
    }

    private static void Report(string name, (double Recall, List<double> Milliseconds) result)
    {
        var sorted = result.Milliseconds.Order().ToList();
        Console.WriteLine($"{name,-40} {result.Recall,9:F3} {sorted[sorted.Count / 2],7:F1} {sorted[(int)(sorted.Count * 0.95)],7:F1}");
    }

    private static async Task<List<int>> PgSearchAsync(NpgsqlConnection connection, float[] query, string where)
    {
        await using var command = new NpgsqlCommand($"SELECT id FROM logs {where} ORDER BY embedding <=> $1 LIMIT 10", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = new PgVector(query) });
        var ids = new List<int>(10);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static async Task<List<int>> QdrantSearchAsync(QdrantClient client, float[] query, SearchParams parameters, Filter? filter)
    {
        var points = await client.QueryAsync("logs", query: query, filter: filter, searchParams: parameters, limit: 10, payloadSelector: false);
        return [.. points.Select(p => (int)p.Id.Num)];
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
