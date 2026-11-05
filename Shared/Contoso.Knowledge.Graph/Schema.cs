using System.Text.RegularExpressions;

namespace Contoso.Knowledge.Graph;

/// <summary>
/// The enterprise schema: a dozen entity types and twenty relations, each with the types it may join. An extractor
/// may only propose a relation whose ends have these types; anything else is dropped before it reaches the graph.
/// </summary>
public static class Schema
{
    public static IReadOnlyDictionary<string, (string From, string To)> Relations { get; } = new Dictionary<string, (string, string)>
    {
        ["INSTANCE_OF"] = ("Equipment", "Model"),
        ["LOCATED_AT"] = ("Equipment", "Site"),
        ["HAS_FITTED"] = ("Equipment", "Part"),
        ["HAS_COMPONENT"] = ("Equipment", "Equipment"),
        ["FITS"] = ("Part", "Model"),
        ["SUPPLIED_BY"] = ("Part", "Supplier"),
        ["SUPERSEDED_BY"] = ("Part", "Part"),
        ["AFFECTS"] = ("ChangeNotice", "Part"),
        ["APPLIES_TO"] = ("ChangeNotice", "Model"),
        ["RAISED_BY"] = ("ChangeNotice", "Person"),
        ["CONCERNS"] = ("Incident", "Model"),
        ["AT"] = ("Incident", "Site"),
        ["FOR_CUSTOMER"] = ("Incident", "Customer"),
        ["INVOLVES"] = ("Incident", "Part"),
        ["REPORTED"] = ("Incident", "ErrorCode"),
        ["UNDER"] = ("Incident", "ChangeNotice"),
        ["HANDLED_BY"] = ("Incident", "Person"),
        ["FOLLOWS"] = ("Incident", "Incident"),
        ["SITE_OF"] = ("Site", "Customer"),
        ["CONTRACT_WITH"] = ("Contract", "Supplier"),
    };

    public static bool Allows(string relation, string fromType, string toType) =>
        Relations.TryGetValue(relation, out var r) && r.From == fromType && r.To == toType;

    /// <summary>Words in a question that point at a relation: "who supplies" asks for SUPPLIED_BY, "replaced by" for SUPERSEDED_BY.</summary>
    public static IReadOnlyDictionary<string, string[]> Cues { get; } = new Dictionary<string, string[]>
    {
        ["SUPPLIED_BY"] = ["supplier", "suppliers", "supplies", "supply", "supplied", "vendor"],
        ["SUPERSEDED_BY"] = ["replaced", "replacement", "replace", "superseded", "supersedes"],
        ["AFFECTS"] = ["affected", "affects", "quarantined"],
        ["APPLIES_TO"] = ["apply", "applies"],
        ["HAS_FITTED"] = ["fitted", "installed"],
        ["INVOLVES"] = ["fitted", "replaced"],
        ["HAS_COMPONENT"] = ["valve"],
        ["RAISED_BY"] = ["raised", "originator"],
        ["REPORTED"] = ["error", "fault", "code"],
        ["FOLLOWS"] = ["later", "follow-up"],
        ["LOCATED_AT"] = ["where", "plant"],
    };

    /// <summary>The relations a question's words point at.</summary>
    public static IReadOnlySet<string> CuedBy(string question)
    {
        var words = question.ToLowerInvariant().Split([' ', ',', '?', '.', '\'', '’'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return Cues.Where(c => c.Value.Any(words.Contains)).Select(c => c.Key).ToHashSet();
    }
}

/// <summary>An entity found in text: its canonical ID, type and name, the surface form and its character span.</summary>
public sealed record EntityMention(string Id, string Type, string Name, string Text, int Start, int End, string Method);

/// <summary>Names the gazetteer knows: suppliers, customers, sites and people, each with its aliases.</summary>
public sealed record GazetteerEntry(string Type, string Name, IReadOnlyList<string> Aliases);

/// <summary>
/// Rules first: Contoso's identifiers follow patterns (PN-4471-B, ECN-2291, SR-2026-1107, E-217), and the names that
/// matter are few enough to list. A match becomes a canonical ID straight away; identifiers are never fuzzy-matched.
/// </summary>
public sealed partial class EntityRecognizer(IReadOnlyList<GazetteerEntry> gazetteer)
{
    private readonly (Regex Pattern, GazetteerEntry Entry)[] _names = [.. gazetteer
        .SelectMany(e => e.Aliases.Prepend(e.Name).Select(alias => (new Regex($@"\b{Regex.Escape(alias)}\b", RegexOptions.IgnoreCase), e)))
        .OrderByDescending(x => x.Item1.ToString().Length)];

    public IReadOnlyList<GazetteerEntry> Gazetteer { get; } = gazetteer;

    public IReadOnlyList<EntityMention> Find(string text, int offset = 0)
    {
        var found = new List<EntityMention>();
        foreach (Match m in Identifier().Matches(text))
        {
            var group = m.Groups.Cast<Group>().Skip(1).First(g => g.Success);
            found.Add(new EntityMention(IdOf(group.Name, m.Value), group.Name, m.Value.ToUpperInvariant(), m.Value, offset + m.Index, offset + m.Index + m.Length, "pattern"));
        }

        foreach (var (pattern, entry) in _names)
        {
            foreach (Match m in pattern.Matches(text))
            {
                var (start, end) = (offset + m.Index, offset + m.Index + m.Length);
                if (found.Any(f => f.Start < end && start < f.End)) continue;
                found.Add(new EntityMention(IdOf(entry.Type, entry.Name), entry.Type, entry.Name, m.Value, start, end, "gazetteer"));
            }
        }

        return [.. found.OrderBy(f => f.Start)];
    }

    /// <summary>Canonical IDs: the type and the identifier as written, or the type and a slug of the name.</summary>
    public static string IdOf(string type, string value) => type switch
    {
        "Part" or "ChangeNotice" or "WorkOrder" or "Incident" or "ErrorCode" or "Equipment" or "Model" or "Contract" => $"{type.ToLowerInvariant()}:{value.ToUpperInvariant()}",
        _ => $"{type.ToLowerInvariant()}:{Slug(value)}",
    };

    public static string Slug(string name) => string.Join('-', Regex.Split(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0));

    [GeneratedRegex(@"\b(?:(?<Part>PN-\d{4}-[A-Z])|(?<ChangeNotice>ECN-\d{4})|(?<WorkOrder>WO-\d{5})|(?<Incident>SR-\d{4}-\d{4})|(?<ErrorCode>E-\d{3}|F0\d{2})|(?<Contract>SC-\d{4}-\d{3})|(?<Equipment>[PV]-\d{3}|GBX-\d{2}|CNV-\d{2}|HG-\d{4})|(?<Model>CP-200|HG-400|VD-75))\b")]
    private static partial Regex Identifier();
}
