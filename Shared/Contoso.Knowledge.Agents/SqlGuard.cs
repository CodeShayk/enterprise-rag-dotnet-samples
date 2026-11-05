using System.Text.Json.Nodes;
using DuckDB.NET.Data;

namespace Contoso.Knowledge.Agents;

/// <summary>Whether a statement may run, why not, and the views it reads.</summary>
public sealed record SqlVerdict(bool Allowed, string Reason, IReadOnlyList<string> Views);

/// <summary>
/// Checks SQL a model wrote before it runs. DuckDB's own parser reads the statement (<c>json_serialize_sql</c>
/// serializes SELECT statements only, so anything else fails here), and the checks walk its syntax tree: exactly one
/// statement, every table an unqualified name of one of the person's views or of a CTE the statement defines, and no
/// table functions, which is how DuckDB reads files, settings and catalogs.
/// </summary>
public static class SqlGuard
{
    public static SqlVerdict Check(DuckDBConnection connection, string sql, IReadOnlyCollection<string> views)
    {
        JsonNode tree;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT json_serialize_sql(CAST(? AS VARCHAR))";
            command.Parameters.Add(new DuckDBParameter(sql));
            tree = JsonNode.Parse((string)command.ExecuteScalar()!)!;
        }

        if (tree["error"]?.GetValue<bool>() == true) return Reject($"Not a valid SELECT statement: {tree["error_message"]}");
        if (tree["statements"] is not JsonArray { Count: 1 } statements) return Reject("Write exactly one SELECT statement.");

        var ctes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var read = new List<string>();
        foreach (var node in Walk(statements[0]!))
        {
            if (node["cte_map"]?["map"] is JsonArray map) ctes.UnionWith(map.Select(e => e!["key"]!.GetValue<string>()));
            switch (node["type"] is JsonValue type && type.TryGetValue<string>(out var name) ? name : null)
            {
                case "TABLE_FUNCTION":
                    return Reject("Table functions aren't allowed; query the tables listed.");
                case "BASE_TABLE":
                    var (catalog, schema, table) = (node["catalog_name"]?.GetValue<string>() ?? "", node["schema_name"]?.GetValue<string>() ?? "", node["table_name"]!.GetValue<string>());
                    if (catalog.Length > 0 || schema.Length > 0) return Reject($"Use the table names as listed, without a schema: {schema}.{table}.");
                    read.Add(table);
                    break;
            }
        }

        var unknown = read.Where(t => !ctes.Contains(t) && !views.Contains(t, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
        return unknown.Count > 0
            ? Reject($"No such table: {string.Join(", ", unknown)}. The tables you can read are {string.Join(", ", views)}.")
            : new SqlVerdict(true, "", [.. read.Where(t => !ctes.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    private static SqlVerdict Reject(string reason) => new(false, reason, []);

    private static IEnumerable<JsonObject> Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                yield return o;
                foreach (var child in o.Select(p => p.Value).OfType<JsonNode>().SelectMany(Walk)) yield return child;
                break;
            case JsonArray a:
                foreach (var child in a.OfType<JsonNode>().SelectMany(Walk)) yield return child;
                break;
        }
    }
}
