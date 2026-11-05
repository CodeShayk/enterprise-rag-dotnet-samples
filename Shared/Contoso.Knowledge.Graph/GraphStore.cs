using System.Data.Common;

namespace Contoso.Knowledge.Graph;

/// <summary>
/// The graph as three relational tables (nodes, edges and the mentions behind both), with the same SQL on DuckDB
/// in-process and on PostgreSQL. A k-hop neighborhood is a recursive CTE; deleting a document version cascades from
/// mentions to edges to nodes in three statements.
/// </summary>
public static class GraphStore
{
    public const string Schema = """
        CREATE TABLE nodes (id VARCHAR PRIMARY KEY, type VARCHAR NOT NULL, name VARCHAR NOT NULL);
        CREATE TABLE edges (id INTEGER PRIMARY KEY, src VARCHAR NOT NULL, rel VARCHAR NOT NULL, dst VARCHAR NOT NULL);
        CREATE TABLE mentions (node_id VARCHAR, edge_id INTEGER, doc VARCHAR NOT NULL, version VARCHAR NOT NULL,
            start_at INTEGER, end_at INTEGER, method VARCHAR, confidence DOUBLE PRECISION);
        CREATE INDEX edges_src ON edges (src);
        CREATE INDEX edges_dst ON edges (dst);
        """;

    /// <summary>
    /// Every node within <c>$2</c> hops of <c>$1</c>, ignoring direction, with its distance. UNION drops repeated
    /// (node, depth) rows and the depth bound stops the recursion, so cycles can't run away.
    /// </summary>
    public const string Hops = """
        WITH RECURSIVE walk(node, depth) AS (
            SELECT CAST($1 AS VARCHAR), 0
            UNION
            SELECT CASE WHEN e.src = w.node THEN e.dst ELSE e.src END, w.depth + 1
            FROM walk w JOIN edges e ON w.node IN (e.src, e.dst)
            WHERE w.depth < $2)
        SELECT node, min(depth) AS hops FROM walk GROUP BY node ORDER BY hops, node
        """;

    public const string RemoveVersion = """
        DELETE FROM mentions WHERE doc = $1 AND version = $2;
        DELETE FROM edges WHERE NOT EXISTS (SELECT 1 FROM mentions m WHERE m.edge_id = edges.id);
        DELETE FROM nodes WHERE NOT EXISTS (SELECT 1 FROM mentions m WHERE m.node_id = nodes.id)
            AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.src = nodes.id OR e.dst = nodes.id);
        """;

    public static async Task CreateAsync(DbConnection connection, KnowledgeGraph graph)
    {
        foreach (var statement in Schema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) await ExecuteAsync(connection, statement);
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var n in graph.Nodes)
        {
            await ExecuteAsync(connection, "INSERT INTO nodes VALUES ($1, $2, $3)", n.Id, n.Type, n.Name);
            foreach (var m in n.Mentions) await InsertMentionAsync(connection, n.Id, null, m);
        }

        foreach (var (e, i) in graph.Edges.Select((e, i) => (e, i)))
        {
            await ExecuteAsync(connection, "INSERT INTO edges VALUES ($1, $2, $3, $4)", i, e.Key.From, e.Key.Relation, e.Key.To);
            foreach (var m in e.Mentions) await InsertMentionAsync(connection, null, i, m);
        }

        await transaction.CommitAsync();
    }

    public static async Task<IReadOnlyList<(string Node, int Hops)>> NeighborhoodAsync(DbConnection connection, string seed, int hops)
    {
        await using var command = Command(connection, Hops, seed, hops);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, int)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)));
        return rows;
    }

    public static async Task<(long Mentions, long Edges, long Nodes)> CountAsync(DbConnection connection) =>
        (await ScalarAsync(connection, "SELECT count(*) FROM mentions"), await ScalarAsync(connection, "SELECT count(*) FROM edges"), await ScalarAsync(connection, "SELECT count(*) FROM nodes"));

    public static async Task RemoveVersionAsync(DbConnection connection, string documentId, string version)
    {
        foreach (var statement in RemoveVersion.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            await ExecuteAsync(connection, statement, [.. statement.Contains("$1", StringComparison.Ordinal) ? new object[] { documentId, version } : []]);
        }
    }

    private static Task InsertMentionAsync(DbConnection connection, string? node, int? edge, Mention m) =>
        ExecuteAsync(connection, "INSERT INTO mentions VALUES ($1, $2, $3, $4, $5, $6, $7, $8)", node, edge, m.DocumentId, m.Version, m.Start, m.End, m.Method, m.Confidence);

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = Command(connection, sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, params object?[] values)
    {
        await using var command = Command(connection, sql, values);
        await command.ExecuteNonQueryAsync();
    }

    private static DbCommand Command(DbConnection connection, string sql, params object?[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
        {
            var parameter = command.CreateParameter();
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
