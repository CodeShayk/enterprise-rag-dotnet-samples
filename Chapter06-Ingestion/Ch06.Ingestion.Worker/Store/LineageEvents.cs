using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ch06.Ingestion.Worker.Store;

/// <summary>
/// Run events in the shape of the OpenLineage specification: an event type, a run, a job and the datasets it read
/// and wrote, each with facets. Kept as JSON lines next to the store, so that a lineage backend can import them.
/// </summary>
public sealed class LineageEvents(string folder, IngestionStore store)
{
    private const string Producer = "https://github.com/contoso/knowledge-assistant/ingestion";
    private const string SchemaUrl = "https://openlineage.io/spec/2-0-2/OpenLineage.json#/definitions/RunEvent";

    public void Emit(string eventType, string runId, string job, IEnumerable<(string Namespace, string Name)> inputs,
        IEnumerable<(string Namespace, string Name)> outputs, JsonObject? runFacets = null)
    {
        var json = new JsonObject
        {
            ["eventType"] = eventType,
            ["eventTime"] = DateTimeOffset.UtcNow.ToString("O"),
            ["producer"] = Producer,
            ["schemaURL"] = SchemaUrl,
            ["run"] = new JsonObject { ["runId"] = runId, ["facets"] = runFacets ?? new JsonObject() },
            ["job"] = new JsonObject { ["namespace"] = "contoso.knowledge.ingestion", ["name"] = job },
            ["inputs"] = new JsonArray([.. inputs.Select(i => (JsonNode)new JsonObject { ["namespace"] = i.Namespace, ["name"] = i.Name })]),
            ["outputs"] = new JsonArray([.. outputs.Select(o => (JsonNode)new JsonObject { ["namespace"] = o.Namespace, ["name"] = o.Name })]),
        };

        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "lineage-events.jsonl"), json.ToJsonString() + "\n");
        store.AddLineageEvent(runId, eventType, job, JsonSerializer.SerializeToElement(json));
    }
}

/// <summary>OpenLineage dataset names: a namespace for the system that holds the data and a name within it.</summary>
public static class Datasets
{
    public const string Store = "sqlite://ingestion.db";

    public static IReadOnlyList<(string Namespace, string Name)> Outputs { get; } = [(Store, "documents"), (Store, "version_chunks"), (Store, "chunks")];

    public static (string Namespace, string Name) Input(string system) => system switch
    {
        "fileshare" => ("file://contoso-fs01", "/engineering"),
        "intranet" => ("https://intranet.contoso.example", "/sitemap.xml"),
        "sharepoint" => ("https://graph.microsoft.com", "/v1.0/drives/contoso-sp"),
        "mail" => ("mailbox://service@contoso.example", "/export"),
        "database" => ("postgres://cmms.contoso.example:5432", "maintenance.work_orders"),
        _ => (system, "/"),
    };
}
