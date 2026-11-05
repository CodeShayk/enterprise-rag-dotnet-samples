using System.Diagnostics;
using System.Security.Cryptography;
using Contoso.Knowledge.Retrieval;
using Npgsql;
using Pgvector;
using Testcontainers.PostgreSql;
using static System.FormattableString;

namespace Ch22.Security.Cli;

/// <summary>
/// One shared table for every tenant's chunks, isolated by PostgreSQL row-level security: the application connects as a
/// role the policy applies to and sets its tenant per transaction, so a query that forgets the tenant still sees only
/// its own rows.
/// </summary>
public static class TenantsCommand
{
    private const string Schema = """
        CREATE EXTENSION IF NOT EXISTS vector;
        CREATE TABLE chunks (id int PRIMARY KEY, tenant text NOT NULL, document text NOT NULL, embedding vector(384) NOT NULL);
        ALTER TABLE chunks ENABLE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON chunks
            USING (tenant = current_setting('app.tenant', true))
            WITH CHECK (tenant = current_setting('app.tenant', true));
        CREATE ROLE knowledge_app LOGIN;
        GRANT SELECT, INSERT ON chunks TO knowledge_app;
        """;

    public static async Task RunAsync(Lab lab)
    {
        await using var postgres = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg17").Build();
        await postgres.StartAsync();
        var owner = postgres.GetConnectionString();
        await Execute(owner, Schema);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await Execute(owner, $"ALTER ROLE knowledge_app PASSWORD '{password}'");

        var embeddings = await lab.Passages.GenerateAsync(lab.Chunks.Select(c => c.Chunk.IndexText));
        var builder = new NpgsqlDataSourceBuilder(owner);
        builder.UseVector();
        await using (var source = builder.Build())
        {
            await using var batch = new NpgsqlBatch(await source.OpenConnectionAsync());
            for (var i = 0; i < lab.Chunks.Count; i++)
            {
                batch.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO chunks VALUES ($1, $2, $3, $4)")
                {
                    Parameters = { new() { Value = i }, new() { Value = lab.Chunks[i].Document.Tenant }, new() { Value = lab.Chunks[i].Document.Id }, new() { Value = new Vector(embeddings[i].Vector) } },
                });
            }

            await batch.ExecuteNonQueryAsync();
        }

        var app = new NpgsqlConnectionStringBuilder(owner) { Username = "knowledge_app", Password = password }.ConnectionString;
        var appBuilder = new NpgsqlDataSourceBuilder(app);
        appBuilder.UseVector();
        await using var appSource = appBuilder.Build();
        var ownerBuilder = new NpgsqlDataSourceBuilder(owner);
        ownerBuilder.UseVector();
        await using var ownerSource = ownerBuilder.Build();

        Console.WriteLine($"{lab.Chunks.Count} chunks of every tenant in one table, PostgreSQL 17 with row-level security, as role knowledge_app");
        Console.WriteLine($"  SELECT count(*) FROM chunks  (no tenant in the query):");
        foreach (var tenant in new[] { "fluid", "service", "motion", "corporate", "" })
        {
            var count = await Scalar(appSource, tenant, "SELECT count(*) FROM chunks");
            Console.WriteLine($"    app.tenant = {(tenant.Length == 0 ? "(not set)" : tenant),-11} {count,4} rows");
        }

        Console.WriteLine($"    as the table owner, a superuser:   {await Scalar(ownerSource, "fluid", "SELECT count(*) FROM chunks"),4} rows (superusers skip the policy)");
        try
        {
            await Scalar(appSource, "fluid", "INSERT INTO chunks VALUES (99999, 'service', 'X', array_fill(0, ARRAY[384])::vector) RETURNING 1");
        }
        catch (PostgresException e)
        {
            Console.WriteLine($"  as fluid, INSERT a row for service: {e.SqlState} {e.MessageText}");
        }

        // The same nearest-neighbor query with and without the policy in the path (owner bypasses it), exact scan.
        var query = new Vector((await lab.Passages.GenerateAsync(["What torque applies to the M12 flange bolts on P-201?"]))[0].Vector);
        foreach (var (name, source) in new[] { ("with row-level security", appSource), ("without it (owner)", ownerSource) })
        {
            var times = new List<double>();
            for (var i = 0; i < 60; i++)
            {
                var clock = Stopwatch.StartNew();
                await Scalar(source, "fluid", "SELECT count(*) FROM (SELECT id FROM chunks WHERE tenant = 'fluid' ORDER BY embedding <=> $1 LIMIT 4) t", query);
                times.Add(clock.Elapsed.TotalMilliseconds);
            }

            Console.WriteLine(Invariant($"  top 4 by cosine for fluid, {name}: p50 {times.Order().Skip(30).First():F2} ms"));
        }
    }

    private static async Task Execute(string connection, string sql)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var command = new NpgsqlCommand(sql, c);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs a query in a transaction that sets the tenant first: <c>set_config(..., true)</c> lasts for the transaction only.</summary>
    private static async Task<object?> Scalar(NpgsqlDataSource source, string tenant, string sql, object? parameter = null)
    {
        await using var c = await source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant', $1, true)", c, tx) { Parameters = { new() { Value = tenant } } })
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, c, tx);
        if (parameter is not null) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        var result = await command.ExecuteScalarAsync();
        await tx.CommitAsync();
        return result;
    }
}
