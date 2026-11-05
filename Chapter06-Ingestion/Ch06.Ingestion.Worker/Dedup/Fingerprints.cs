using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Ch06.Ingestion.Worker.Dedup;

/// <summary>
/// Fingerprints for duplicate detection. Exact duplicates share the SHA-256 of their normalized text, which
/// survives re-saving, re-encoding and reformatting. Near-duplicates share most of their word shingles: MinHash
/// estimates that overlap (the Jaccard similarity) from 128 numbers per document, LSH banding finds candidate
/// pairs without comparing every pair, and SimHash folds a document into 64 bits whose Hamming distance tracks similarity.
/// </summary>
public static class Fingerprints
{
    public const int Shingle = 5;
    public const int Permutations = 128;
    public const int Bands = 32;
    public const int Rows = Permutations / Bands;

    /// <summary>Lowercase letters and digits separated by single spaces: what's left when formatting is ignored.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = true;
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static string TextHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));

    /// <summary>The set of five-word shingles, each hashed to 64 bits.</summary>
    public static HashSet<ulong> Shingles(string text)
    {
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shingles = new HashSet<ulong>();
        for (var i = 0; i + Shingle <= Math.Max(words.Length, Shingle); i++)
        {
            shingles.Add(Hash64(string.Join(' ', words.Skip(i).Take(Shingle))));
        }

        return shingles;
    }

    public static double Jaccard(HashSet<ulong> a, HashSet<ulong> b) =>
        a.Count + b.Count == 0 ? 1 : (double)a.Count(b.Contains) / (a.Count + b.Count - a.Count(b.Contains));

    /// <summary>
    /// The MinHash signature: for each of 128 hash functions, the smallest hash over the shingles. Two documents
    /// agree in any one position with probability equal to their Jaccard similarity.
    /// </summary>
    public static ulong[] MinHash(HashSet<ulong> shingles)
    {
        var signature = new ulong[Permutations];
        Array.Fill(signature, ulong.MaxValue);
        foreach (var shingle in shingles)
        {
            for (var i = 0; i < Permutations; i++)
            {
                var h = Mix(shingle ^ Seeds[i]);
                if (h < signature[i]) signature[i] = h;
            }
        }

        return signature;
    }

    public static double EstimatedJaccard(ulong[] a, ulong[] b) => (double)a.Zip(b).Count(p => p.First == p.Second) / a.Length;

    /// <summary>
    /// LSH bands: 32 bands of 4 rows. Two documents become candidates when all four rows of any band agree,
    /// which happens mostly above a similarity of about (1/32)^(1/4) = 0.42.
    /// </summary>
    public static IEnumerable<string> BandKeys(ulong[] signature)
    {
        for (var band = 0; band < Bands; band++)
        {
            var key = new StringBuilder($"{band}:");
            for (var row = 0; row < Rows; row++)
            {
                key.Append(signature[band * Rows + row].ToString("x", CultureInfo.InvariantCulture)).Append('.');
            }

            yield return key.ToString();
        }
    }

    /// <summary>SimHash: every shingle votes on each of 64 bits; the fingerprint keeps the majority. Similar documents differ in few bits.</summary>
    public static ulong SimHash(HashSet<ulong> shingles)
    {
        Span<int> votes = stackalloc int[64];
        foreach (var shingle in shingles)
        {
            var h = Mix(shingle);
            for (var bit = 0; bit < 64; bit++)
            {
                votes[bit] += ((h >> bit) & 1) == 1 ? 1 : -1;
            }
        }

        ulong fingerprint = 0;
        for (var bit = 0; bit < 64; bit++)
        {
            if (votes[bit] > 0) fingerprint |= 1UL << bit;
        }

        return fingerprint;
    }

    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>FNV-1a over UTF-8, then mixed: fast and stable across processes (string.GetHashCode isn't).</summary>
    public static ulong Hash64(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return Mix(hash);
    }

    // SplitMix64's finalizer: spreads every input bit over the output.
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static readonly ulong[] Seeds = [.. Enumerable.Range(1, Permutations).Select(i => Mix((ulong)i * 0x9E3779B97F4A7C15UL))];
}
