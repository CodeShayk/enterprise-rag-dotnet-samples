using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Ch06.Ingestion.Worker.Store;

/// <summary>
/// The lineage tables in PostgreSQL, for a deployment where several workers share one store: the same records as
/// the SQLite store, with events as <c>jsonb</c> so that they can be queried by dataset. Chapter 6 runs on SQLite;
/// this store is exercised by a Testcontainers test that runs only when Docker is available.
/// </summary>
public sealed class PostgresLineageStore(NpgsqlDataSource dataSource)
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS lineage_events (
            run_id     text        NOT NULL,
            event_time timestamptz NOT NULL,
            event_type text        NOT NULL,
            job        text        NOT NULL,
            event      jsonb       NOT NULL);
        CREATE INDEX IF NOT EXISTS lineage_events_run ON lineage_events (run_id);
        CREATE INDEX IF NOT EXISTS lineage_events_inputs ON lineage_events USING gin ((event -> 'inputs'));
        CREATE TABLE IF NOT EXISTS documents (
            doc_id text NOT NULL, version text NOT NULL, system text, uri text, sha256 text, text_hash text,
            parser text, effective_from date, effective_to date, superseded_by text, duplicate_of text,
            tombstoned boolean NOT NULL DEFAULT false, run_id text, PRIMARY KEY (doc_id, version));
        """;

    public async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(Schema);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddEventAsync(string runId, string eventType, string job, JsonElement json, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO lineage_events (run_id, event_time, event_type, job, event) VALUES ($1, now(), $2, $3, $4)");
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue(job);
        command.Parameters.Add(new NpgsqlParameter { Value = json.GetRawText(), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The runs that read a given input dataset, newest first: "which runs touched this file?"</summary>
    public async Task<IReadOnlyList<string>> RunsReadingAsync(string inputName, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT DISTINCT run_id, max(event_time) FROM lineage_events
            WHERE event -> 'inputs' @> jsonb_build_array(jsonb_build_object('name', $1::text))
            GROUP BY run_id ORDER BY max(event_time) DESC
            """);
        command.Parameters.AddWithValue(inputName);
        var runs = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) runs.Add(reader.GetString(0));
        return runs;
    }
}
