using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Contoso.Corpus.Generator.Writers;

/// <summary>
/// Office files are zip packages, and the libraries that write them add randomness: entry times, a
/// core-properties part named by a GUID and random relationship IDs. Rewriting them makes the corpus
/// byte-for-byte reproducible.
/// </summary>
internal static partial class Zip
{
    public static byte[] Normalize(byte[] package, DateTime timestamp)
    {
        var entries = new List<(string Name, byte[] Data)>();
        using (var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        {
            foreach (var entry in source.Entries)
            {
                using var data = new MemoryStream();
                using (var s = entry.Open())
                {
                    s.CopyTo(data);
                }

                entries.Add((entry.FullName, data.ToArray()));
            }
        }

        // Map every random name to a stable one, in the order the entries appear.
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, data) in entries)
        {
            if (CorePropertiesName().Match(name) is { Success: true } core)
            {
                renames.TryAdd(core.Groups[1].Value, "core");
            }

            if (name.EndsWith(".rels", StringComparison.Ordinal))
            {
                foreach (Match id in RandomRelationshipId().Matches(Encoding.UTF8.GetString(data)))
                {
                    renames.TryAdd(id.Groups[1].Value, $"rIdX{renames.Count + 1}");
                }
            }
        }

        // Zip timestamps can't predate 1980 and are stored without a time zone.
        var time = new DateTimeOffset(DateTime.SpecifyKind(timestamp < new DateTime(1980, 1, 2) ? new DateTime(1980, 1, 2) : timestamp, DateTimeKind.Unspecified), TimeSpan.Zero);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = target.CreateEntry(Rename(name, renames), CompressionLevel.Optimal);
                entry.LastWriteTime = time;
                using var s = entry.Open();
                s.Write(IsText(name) ? Encoding.UTF8.GetBytes(Rename(Encoding.UTF8.GetString(data), renames)) : data);
            }
        }

        return output.ToArray();
    }

    private static bool IsText(string name) =>
        name.EndsWith(".xml", StringComparison.Ordinal) || name.EndsWith(".rels", StringComparison.Ordinal) || name.EndsWith(".psmdcp", StringComparison.Ordinal);

    private static string Rename(string text, Dictionary<string, string> renames)
    {
        foreach (var (from, to) in renames)
        {
            text = text.Replace(from, to, StringComparison.Ordinal);
        }

        return text;
    }

    [GeneratedRegex(@"core-properties/([0-9a-f]{32})\.psmdcp$")]
    private static partial Regex CorePropertiesName();

    [GeneratedRegex(@"Id=""(R[0-9a-f]{16})""")]
    private static partial Regex RandomRelationshipId();
}
