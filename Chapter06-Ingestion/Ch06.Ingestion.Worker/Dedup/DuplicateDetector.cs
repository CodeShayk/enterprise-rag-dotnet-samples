using Contoso.Knowledge.Documents;

namespace Ch06.Ingestion.Worker.Dedup;

/// <summary>A pair of documents that look alike, with the evidence.</summary>
public sealed record NearDuplicate(string A, string B, double EstimatedJaccard, double Jaccard, int SimHashDistance);

/// <summary>
/// Finds exact and near-duplicate documents across all sources, and the template blocks a site repeats on every
/// page. Versions of one document are not duplicates of each other: versioning handles them (Chapter 6.6).
/// </summary>
public sealed class DuplicateDetector
{
    private readonly Dictionary<string, (HashSet<ulong> Shingles, ulong[] Signature, ulong SimHash, string LogicalId)> _documents = [];
    private readonly Dictionary<string, List<string>> _buckets = [];
    private readonly Dictionary<string, string> _byTextHash = [];

    /// <summary>The key of an earlier document with the same normalized text, or <see langword="null"/>.</summary>
    public string? AddExact(string key, string textHash) =>
        _byTextHash.TryAdd(textHash, key) ? null : _byTextHash[textHash];

    public void AddNear(string key, string logicalId, string text)
    {
        var shingles = Fingerprints.Shingles(text);
        var signature = Fingerprints.MinHash(shingles);
        _documents[key] = (shingles, signature, Fingerprints.SimHash(shingles), logicalId);
        foreach (var band in Fingerprints.BandKeys(signature))
        {
            if (!_buckets.TryGetValue(band, out var members)) _buckets[band] = members = [];
            members.Add(key);
        }
    }

    /// <summary>Candidate pairs from shared LSH buckets, confirmed with the exact Jaccard similarity of their shingles.</summary>
    public IReadOnlyList<NearDuplicate> NearDuplicates(double threshold = 0.5)
    {
        var pairs = _buckets.Values.Where(b => b.Count > 1)
            .SelectMany(b => b.SelectMany((x, i) => b.Skip(i + 1).Select(y => string.CompareOrdinal(x, y) < 0 ? (x, y) : (y, x))))
            .Distinct();
        var result = new List<NearDuplicate>();
        foreach (var (a, b) in pairs)
        {
            var (da, db) = (_documents[a], _documents[b]);
            if (da.LogicalId == db.LogicalId) continue;   // versions of one document, not duplicates
            var jaccard = Fingerprints.Jaccard(da.Shingles, db.Shingles);
            if (jaccard >= threshold)
            {
                result.Add(new NearDuplicate(a, b, Fingerprints.EstimatedJaccard(da.Signature, db.Signature), jaccard, Fingerprints.Hamming(da.SimHash, db.SimHash)));
            }
        }

        return [.. result.OrderByDescending(r => r.Jaccard)];
    }

    /// <summary>
    /// Template blocks: text that appears on at least half of a site's pages (and on at least three). Navigation,
    /// footers and "related" lists repeat; content doesn't. Returns the normalized texts to treat as boilerplate.
    /// </summary>
    public static IReadOnlySet<string> TemplateBlocks(IEnumerable<CanonicalDocument> pages, double share = 0.5)
    {
        var list = pages.ToList();
        var counts = list.SelectMany(p => p.Blocks.Select(b => Fingerprints.Normalize(b.ToMarkdown())).Where(t => t.Length > 0).Distinct())
            .GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var minimum = Math.Max(3, (int)Math.Ceiling(list.Count * share));
        return counts.Where(c => c.Value >= minimum).Select(c => c.Key).ToHashSet();
    }
}
