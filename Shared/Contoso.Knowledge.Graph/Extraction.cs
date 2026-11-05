using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Parsing;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Graph;

/// <summary>A relation an extractor proposes, with the span it read it from.</summary>
public sealed record Triple(EntityMention From, string Relation, EntityMention To, int Start, int End, string Method, double Confidence);

/// <summary>
/// Relations without a model. Table rows state relations in their columns: a key-value table ("Field | Value") relates
/// its subject (the tag or the change notice) to the first entity of each type in every other row; any other table
/// relates the entities of one row to each other. A document whose subject is an entity (a change notice, a service
/// report) relates that subject to what its prose mentions. The schema decides the relation: each pair of types has
/// at most one.
/// </summary>
public static class RuleExtractor
{
    public static IEnumerable<Triple> FromTables(ChunkSource source, EntityRecognizer recognizer)
    {
        foreach (var span in source.Projection.Blocks)
        {
            if (source.Document.Blocks[span.Index] is not TableBlock table) continue;
            var headers = TableSerializer.HeaderPaths(table);
            var rows = TableSerializer.DataRows(table).ToList();
            var keyValue = headers.Count == 2 && headers[0].EndsWith("Field", StringComparison.OrdinalIgnoreCase);
            var at = RowStarts(source.Text, span, rows);
            if (keyValue)
            {
                var subject = rows.Select((r, i) => recognizer.Find(string.Join(" ", r), at[i]).FirstOrDefault(e => e.Type is "Equipment" or "ChangeNotice" or "Incident"))
                    .FirstOrDefault(e => e is not null) ?? Subject(source, recognizer);
                if (subject is null) continue;
                for (var i = 0; i < rows.Count; i++)
                {
                    var firsts = recognizer.Find(rows[i].Count > 1 ? rows[i][1] : "", at[i]).Where(e => e.Id != subject.Id).GroupBy(e => e.Type).Select(g => g.First());
                    foreach (var e in firsts)
                    {
                        if (Relate(subject, e, at[i], at[i] + RowLength(rows[i]), "table") is { } t) yield return t;
                    }
                }

                continue;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var found = recognizer.Find(string.Join(" | ", rows[i]), at[i]).DistinctBy(e => e.Id).ToList();
                for (var a = 0; a < found.Count; a++)
                {
                    for (var b = a + 1; b < found.Count; b++)
                    {
                        if (Relate(found[a], found[b], at[i], at[i] + RowLength(rows[i]), "table") is { } t) yield return t;
                    }
                }
            }
        }
    }

    /// <summary>The document's own entity, when its ID is one: a change notice or a service report is about itself.</summary>
    public static EntityMention? Subject(ChunkSource source, EntityRecognizer recognizer) =>
        recognizer.Find(source.Id).FirstOrDefault(e => e.Text == source.Id && e.Type is "ChangeNotice" or "Incident");

    /// <summary>The document's subject related to every entity its prose mentions, where the schema has a relation for the pair.</summary>
    public static IEnumerable<Triple> FromSubject(ChunkSource source, EntityRecognizer recognizer)
    {
        if (Subject(source, recognizer) is not { } subject) yield break;
        foreach (var span in source.Projection.Blocks.Where(b => source.Document.Blocks[b.Index] is not TableBlock))
        {
            foreach (var e in recognizer.Find(source.Text[span.Start..span.End], span.Start).Where(e => e.Id != subject.Id))
            {
                if (Relate(subject, e, e.Start, e.End, "subject", 0.6) is { } t) yield return t;
            }
        }
    }

    /// <summary>The schema's relation for two entities in either direction; two parts are related in reading order.</summary>
    public static Triple? Relate(EntityMention a, EntityMention b, int start, int end, string method, double confidence = 0.9)
    {
        foreach (var (relation, (from, to)) in Schema.Relations)
        {
            if (from == a.Type && to == b.Type) return new Triple(a, relation, b, start, end, method, confidence);
            if (from == b.Type && to == a.Type) return new Triple(b, relation, a, start, end, method, confidence);
        }

        return null;
    }

    private static int[] RowStarts(string text, BlockSpan span, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var (starts, from) = (new int[rows.Count], span.Start);
        for (var i = 0; i < rows.Count; i++)
        {
            var cell = rows[i].FirstOrDefault(c => c.Length > 0) ?? "";
            var found = cell.Length == 0 ? -1 : text.IndexOf(cell, from, span.End - from, StringComparison.Ordinal);
            starts[i] = found < 0 ? from : found;
            from = starts[i];
        }

        return starts;
    }

    private static int RowLength(IReadOnlyList<string> row) => row.Sum(c => c.Length + 3);
}

/// <summary>
/// Selective relation extraction with a small model. Only a chunk whose entities admit some relation in the schema is
/// sent, with those entities as the only IDs allowed and only the relations their types allow; every proposal is
/// checked against the schema again. A budget caps the calls.
/// </summary>
public sealed class ModelRelationExtractor(IChatClient chat, int maxCalls = 200)
{
    public int Calls { get; private set; }

    public TimeSpan Time { get; private set; }

    /// <summary>Relations kept, of which the model named an entity by its text instead of its ID, or wrote the pair backwards; and proposals dropped.</summary>
    public int Proposed { get; private set; }

    public int Renamed { get; private set; }

    public int Flipped { get; private set; }

    public int Rejected { get; private set; }

    /// <summary>
    /// The entity a reply names: by its ID, or, since a small model often drops the type prefix, by the identifier,
    /// name or slug of exactly one entity in the passage.
    /// </summary>
    public static EntityMention? Entity(IReadOnlyList<EntityMention> entities, string? named)
    {
        if (named is null) return null;
        if (entities.FirstOrDefault(e => e.Id == named) is { } exact) return exact;
        var matches = entities.Where(e => string.Equals(e.Id[(e.Id.IndexOf(':') + 1)..], named, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Name, named, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Text, named, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public static IReadOnlyList<string> AllowedFor(IReadOnlyList<EntityMention> entities) =>
        [.. Schema.Relations.Where(r => entities.Any(e => e.Type == r.Value.From) && entities.Any(e => e.Type == r.Value.To && !(r.Value.From == r.Value.To && entities.Count(x => x.Type == e.Type) < 2))).Select(r => r.Key)];

    public async Task<IReadOnlyList<Triple>> ExtractAsync(ChunkSource source, Chunk chunk, IReadOnlyList<EntityMention> entities, CancellationToken cancellationToken = default)
    {
        var distinct = entities.DistinctBy(e => e.Id).ToList();
        var allowed = AllowedFor(distinct);
        if (allowed.Count == 0 || Calls >= maxCalls) return [];
        Calls++;
        var prompt = new StringBuilder($"You extract relationships for a maintenance knowledge graph from one passage of the document {source.Id} ({source.Title}).\n\n");
        prompt.AppendLine("Entities in the passage (use these IDs only):");
        foreach (var e in distinct) prompt.AppendLine($"- {e.Id} ({e.Type}): {e.Text}");
        prompt.AppendLine("\nAllowed relations (from type -> to type):");
        foreach (var r in allowed) prompt.AppendLine($"- {r}: {Schema.Relations[r].From} -> {Schema.Relations[r].To}");
        prompt.AppendLine($"\nPassage:\n{chunk.Text}\n");
        prompt.Append("Answer with JSON only: {\"relations\": [{\"from\": \"<id>\", \"relation\": \"<NAME>\", \"to\": \"<id>\"}]}. Include a relation only if the passage states it; return an empty list if it states none.");
        var clock = Stopwatch.StartNew();
        var response = await chat.GetResponseAsync(prompt.ToString(), new ChatOptions { Temperature = 0, MaxOutputTokens = 300, ResponseFormat = ChatResponseFormat.Json }, cancellationToken);
        // A recorded reply carries the time of the call that made it (Chapter 14's recorder); a live one is timed here.
        Time += response.AdditionalProperties?.TryGetValue("recorded.totalMs", out var recorded) == true && recorded is not null
            ? TimeSpan.FromMilliseconds(Convert.ToDouble(recorded, System.Globalization.CultureInfo.InvariantCulture))
            : clock.Elapsed;
        var triples = new List<Triple>();
        try
        {
            using var json = JsonDocument.Parse(response.Text);
            if (!json.RootElement.TryGetProperty("relations", out var list) || list.ValueKind != JsonValueKind.Array) return [];
            foreach (var r in list.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                string? Get(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var (a, b, relation) = (Entity(distinct, Get("from")), Entity(distinct, Get("to")), Get("relation"));
                if (a is null || b is null || relation is null || a.Id == b.Id || !allowed.Contains(relation))
                {
                    Rejected++;
                    continue;
                }

                var renamed = Get("from") != a.Id || Get("to") != b.Id;
                if (Schema.Allows(relation, a.Type, b.Type)) triples.Add(new Triple(a, relation, b, chunk.Start, chunk.End, "model", 0.7));
                else if (Schema.Allows(relation, b.Type, a.Type))
                {
                    Flipped++;
                    triples.Add(new Triple(b, relation, a, chunk.Start, chunk.End, "model", 0.7));
                }
                else
                {
                    Rejected++;
                    continue;
                }

                Renamed += renamed ? 1 : 0;
            }
        }
        catch (JsonException)
        {
            // A reply that isn't JSON proposes nothing.
        }

        Proposed += triples.Count;
        return triples;
    }
}
