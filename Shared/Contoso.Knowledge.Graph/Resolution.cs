using System.Numerics.Tensors;

namespace Contoso.Knowledge.Graph;

/// <summary>One decision of entity resolution, kept for audit: what was merged into what, by which test and why.</summary>
public sealed record MergeDecision(string Kept, string Merged, string Type, string Method, double Score, bool Applied, string Reason);

/// <summary>
/// Entity resolution for names. Names are normalized (case, punctuation, legal suffixes), blocked by type and first
/// letter, and compared by Jaro-Winkler similarity and token containment; an embedding model, when given, must agree.
/// Identifiers never go through fuzzy matching: PN-4471-B and PN-4471-C are 0.96 alike and different parts. A pair a
/// person has split stays apart, because a split is a rule, not an edit, and every decision is logged.
/// </summary>
public sealed class EntityResolver(Func<string, float[]>? embed = null, double threshold = 0.92, double embeddingThreshold = 0.8)
{
    private static readonly HashSet<string> IdentifierTypes = ["Part", "ChangeNotice", "WorkOrder", "Incident", "ErrorCode", "Equipment", "Model", "Contract"];
    private static readonly string[] Suffixes = ["ltd", "inc", "llc", "gmbh", "plc", "co", "company", "corporation"];

    public HashSet<(string, string)> KeepApart { get; } = [];

    public List<MergeDecision> Log { get; } = [];

    /// <summary>Maps each candidate (type and name) to the canonical name it resolves to: a known name, or itself.</summary>
    public IReadOnlyDictionary<(string Type, string Name), string> Resolve(IEnumerable<(string Type, string Name)> known, IEnumerable<(string Type, string Name)> candidates)
    {
        var canon = known.Distinct().ToList();
        var map = new Dictionary<(string, string), string>();
        foreach (var (type, name) in candidates.Distinct())
        {
            if (IdentifierTypes.Contains(type))
            {
                map[(type, name)] = name.Trim().ToUpperInvariant();
                continue;
            }

            var normalized = Normalize(name);
            var block = canon.Where(k => k.Type == type && Normalize(k.Name) is { Length: > 0 } n && n[0] == normalized.FirstOrDefault()).ToList();
            var best = block.Select(k => (k.Name, Score: Score(normalized, Normalize(k.Name)))).OrderByDescending(x => x.Score).FirstOrDefault();
            if (best.Name is null || best.Score < threshold || best.Name == name)
            {
                map[(type, name)] = name;
                continue;
            }

            var apart = KeepApart.Contains((best.Name, name)) || KeepApart.Contains((name, best.Name));
            var cosine = embed is null ? 1.0 : TensorPrimitives.CosineSimilarity(embed(name), embed(best.Name));
            var applied = !apart && cosine >= embeddingThreshold;
            Log.Add(new MergeDecision(best.Name, name, type, normalized == Normalize(best.Name) ? "normalized" : "fuzzy", Math.Round(Math.Min(best.Score, cosine), 3), applied,
                apart ? "kept apart by a split" : applied ? $"Jaro-Winkler or containment {best.Score:F2}, embedding {cosine:F2}" : $"embedding {cosine:F2} below {embeddingThreshold}"));
            map[(type, name)] = applied ? best.Name : name;
        }

        return map;
    }

    public static string Normalize(string name)
    {
        var words = name.ToLowerInvariant().Split([' ', ',', '.', '\'', '’', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Suffixes.Contains(w) && w != "s").ToList();
        return string.Join(' ', words);
    }

    /// <summary>The larger of Jaro-Winkler similarity and token containment (every word of one name is in the other).</summary>
    public static double Score(string a, string b)
    {
        var (wa, wb) = (a.Split(' '), b.Split(' '));
        var contained = wa.All(wb.Contains) || wb.All(wa.Contains);
        return contained ? 1.0 : JaroWinkler(a, b);
    }

    public static double JaroWinkler(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var window = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        var (matchedA, matchedB) = (new bool[a.Length], new bool[b.Length]);
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            for (var j = Math.Max(0, i - window); j < Math.Min(b.Length, i + window + 1); j++)
            {
                if (matchedB[j] || a[i] != b[j]) continue;
                (matchedA[i], matchedB[j]) = (true, true);
                matches++;
                break;
            }
        }

        if (matches == 0) return 0;
        var (k, transpositions) = (0, 0);
        for (var i = 0; i < a.Length; i++)
        {
            if (!matchedA[i]) continue;
            while (!matchedB[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }

        var jaro = (matches / (double)a.Length + matches / (double)b.Length + (matches - transpositions / 2.0) / matches) / 3;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix]) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }
}
