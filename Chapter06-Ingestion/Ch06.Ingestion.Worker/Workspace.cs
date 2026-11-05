using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ch06.Ingestion.Worker.Sources;
using Contoso.Knowledge.Documents;

namespace Ch06.Ingestion.Worker;

/// <summary>
/// Chapter 6's working copy of the sources under <c>.state/ch06/sources</c>. The generated corpus is never changed;
/// <see cref="Reset"/> copies it, and <see cref="SimulateChanges"/> edits the copy the way a week at Contoso would.
/// </summary>
public sealed class Workspace(string folder)
{
    public string Folder => folder;

    public string Sources => Path.Combine(folder, "sources");

    public string Database => Path.Combine(folder, "ingestion.db");

    public string Artifacts => Path.Combine(folder, "artifacts");

    public static Workspace Default => new(Path.GetFullPath(Path.Combine(GeneratedCorpus.Root, "..", ".state", "ch06")));

    public IReadOnlyDictionary<string, RegisterEntry> Register() =>
        JsonSerializer.Deserialize<List<RegisterEntry>>(File.ReadAllText(Path.Combine(Sources, "manifest.json")), DocumentJson.Options)!
            .ToDictionary(e => e.Path, StringComparer.Ordinal);

    /// <summary>Copies the corpus with its timestamps, plus one file that every share has: a PDF that won't open.</summary>
    public void Reset()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        foreach (var file in Directory.EnumerateFiles(GeneratedCorpus.Sources, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Sources, Path.GetRelativePath(GeneratedCorpus.Sources, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }

        // A transfer that stopped halfway: the first 40 KB of a real PDF.
        var source = File.ReadAllBytes(Path.Combine(Sources, "fileshare", "fluid", "manuals", "CP-200 Troubleshooting Guide 2019.pdf"));
        var broken = "fileshare/fluid/manuals/CP-200 Parts List 2018.pdf";
        File.WriteAllBytes(Path.Combine(Sources, broken), source[..40_000]);
        AddToRegister(broken, "CP200-PARTS-2018", "1", "manual", "CP-200 Parts List (2018)", "fluid", ["fluid", "service"], "2018-05-01");
    }

    /// <summary>A week of changes, one of each kind the connectors must tell apart.</summary>
    public IReadOnlyList<string> SimulateChanges()
    {
        var log = new List<string>();
        var now = new DateTime(2027, 2, 1, 9, 0, 0, DateTimeKind.Utc);

        // 1. Opened and saved again in Word: new bytes and timestamp, same content.
        var sop = Path.Combine(Sources, "fileshare/fluid/sops/SOP-FS-007 Pump isolation and lockout.docx");
        Resave(sop, now);
        log.Add("re-saved without changes: fileshare/fluid/sops/SOP-FS-007 Pump isolation and lockout.docx");

        // 2. Copied back from a backup: new timestamp, same bytes.
        var wi = Path.Combine(Sources, "fileshare/fluid/work-instructions/WI-FS-031 Seal-water strainer cleaning.docx");
        File.SetLastWriteTimeUtc(wi, now);
        log.Add("timestamp only: fileshare/fluid/work-instructions/WI-FS-031 Seal-water strainer cleaning.docx");

        // 3. A paragraph added to a bulletin in place, without a new revision.
        var hotWork = Path.Combine(Sources, "intranet/safety/sb-0947.html");
        File.WriteAllText(hotWork, File.ReadAllText(hotWork).Replace("</article>",
            "<p>From March 1, 2027, hot work permits are issued in the permit app. Paper permits are no longer accepted.</p>\n</article>"));
        File.SetLastWriteTimeUtc(hotWork, now);
        log.Add("edited in place: intranet/safety/sb-0947.html");

        // 4. A bulletin withdrawn from the intranet.
        File.Delete(Path.Combine(Sources, "intranet/safety/sb-0901.html"));
        RemoveFromSitemap("safety/sb-0901.html");
        log.Add("deleted: intranet/safety/sb-0901.html");

        // 5. A new bulletin.
        var added = Path.Combine(Sources, "intranet/safety/sb-0961.html");
        File.WriteAllText(added, File.ReadAllText(Path.Combine(Sources, "intranet/safety/sb-0915.html"))
            .Replace("SB-0915", "SB-0961").Replace("Working at height in pump galleries", "Pressure testing seal-water lines")
            .Replace("Use a mobile elevating work platform or a scaffold with guardrails for work more than 2 meters above the floor of a pump gallery. Ladders are for access only, not for work.",
                     "Test repaired seal-water lines at 1.5 times their working pressure, with water, never with air.")
            .Replace("Close the gallery below to traffic while anyone works overhead, and tie off tools so that they can't fall.",
                     "Keep everyone outside a 3-meter zone around the line until the test pressure has been released."));
        File.SetLastWriteTimeUtc(added, now);
        AddToSitemap("safety/sb-0961.html");
        AddToRegister("intranet/safety/sb-0961.html", "SB-0961", "1", "safety-bulletin", "Safety bulletin SB-0961: Pressure testing seal-water lines", "corporate", ["employees"], "2027-02-01", system: "intranet");
        log.Add("added: intranet/safety/sb-0961.html");

        // 6. A new version: P-202 got the new seal, so its asset record has revision 2.
        var p202 = Path.Combine(Sources, "intranet/assets/p-202.html");
        File.WriteAllText(p202, File.ReadAllText(p202)
            .Replace("PN-4471-B, fitted August 2019; replace with PN-4471-C at the next seal change (ECN-2291)", "PN-4471-C since January 2027 (ECN-2291)")
            .Replace("content=\"1\"", "content=\"2\""));
        File.SetLastWriteTimeUtc(p202, now);
        var register = Register();
        var p202Entry = register["intranet/assets/p-202.html"];
        AddToRegister("intranet/assets/p-202.html", "P202-ASSET", "2", "asset-record", "Asset record: pump P-202", "fluid", ["fluid", "service"], "2027-01-25", system: "intranet",
            supersedes: ["P202-ASSET rev 1"], owner: p202Entry.Metadata.GetProperty("owner").GetString());
        log.Add("new version: intranet/assets/p-202.html (revision 2, effective 2027-01-25)");

        // 7. A SharePoint document updated: Graph's delta query reports it.
        var policy = Path.Combine(Sources, "sharepoint/it/IT-POL-003 Using AI tools.docx");
        ReplaceInDocx(policy, "Use the Contoso Knowledge Assistant for questions about Contoso's documents.",
            "Use the Contoso Knowledge Assistant for questions about Contoso's documents and procedures.");
        File.SetLastWriteTimeUtc(policy, now);
        log.Add("edited: sharepoint/it/IT-POL-003 Using AI tools.docx");

        // 8. One speaker note corrected in a long deck: only the chunk that holds it changes.
        var deck = Path.Combine(Sources, "fileshare/fluid/training/CP-200 seal replacement training.pptx");
        ReplaceInPackage(deck, "ppt/notesSlides/", "Watch the seal for the first hour.", "Watch the seal for the first two hours.");
        File.SetLastWriteTimeUtc(deck, now);
        log.Add("edited: fileshare/fluid/training/CP-200 seal replacement training.pptx (one speaker note)");

        // 9. A new work order in the maintenance system.
        File.AppendAllText(Path.Combine(Sources, "database/work-orders.csv"),
            "WO-88701,P-202,Riverside,CM,P2,\"chg mech seal PN-4471-B -> PN-4471-C per ECN-2291, gland nuts 25Nm\",closed,2027-01-22,2027-01-26,2027-01-25,mokoro,2027-02-01T09:00:00Z\r\n");
        log.Add("new row: database/work-orders.csv (WO-88701)");
        return log;
    }

    private static void Resave(string path, DateTime time)
    {
        // Same entries, new zip timestamps: what Word does to an unchanged document.
        var entries = new List<(string Name, byte[] Data)>();
        using (var zip = ZipFile.OpenRead(path))
        {
            foreach (var entry in zip.Entries)
            {
                using var s = entry.Open();
                using var m = new MemoryStream();
                s.CopyTo(m);
                entries.Add((entry.FullName, m.ToArray()));
            }
        }

        File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                entry.LastWriteTime = time;
                using var s = entry.Open();
                s.Write(data);
            }
        }

        File.SetLastWriteTimeUtc(path, time);
    }

    private static void ReplaceInDocx(string path, string from, string to) => ReplaceInPackage(path, "word/document.xml", from, to);

    private static void ReplaceInPackage(string path, string partPrefix, string from, string to)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.Entries.First(e => e.FullName.StartsWith(partPrefix, StringComparison.Ordinal) && Contains(e, from));
        string xml;
        using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
        var name = entry.FullName;
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(xml.Replace(from, to));
    }

    private static bool Contains(ZipArchiveEntry entry, string text)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd().Contains(text, StringComparison.Ordinal);
    }

    private void AddToSitemap(string page)
    {
        var path = Path.Combine(Sources, "intranet", "sitemap.xml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("</urlset>",
            $"  <url><loc>https://intranet.contoso.example/{page}</loc><lastmod>2027-02-01</lastmod></url>\n</urlset>"));
    }

    private void RemoveFromSitemap(string page)
    {
        var path = Path.Combine(Sources, "intranet", "sitemap.xml");
        File.WriteAllLines(path, File.ReadAllLines(path).Where(l => !l.Contains(page, StringComparison.Ordinal)));
    }

    private void AddToRegister(string path, string id, string version, string type, string title, string tenant, string[] groups, string from,
        string system = "fileshare", string[]? supersedes = null, string? owner = null)
    {
        var file = Path.Combine(Sources, "manifest.json");
        var register = JsonNode.Parse(File.ReadAllText(file))!.AsArray();
        foreach (var existing in register.Where(e => e!["path"]!.GetValue<string>() == path).ToList()) register.Remove(existing);
        register.Add(new JsonObject
        {
            ["path"] = path, ["system"] = system, ["id"] = id, ["version"] = version, ["mediaType"] = MediaTypes.FromFileName(path),
            ["lastModified"] = DateTimeOffset.UtcNow.ToString("O"), ["length"] = 0, ["sha256"] = "",
            ["metadata"] = new JsonObject
            {
                ["title"] = title, ["tenant"] = tenant, ["documentType"] = type, ["sensitivity"] = "internal",
                ["allowedGroups"] = new JsonArray([.. groups.Select(g => (JsonNode)g)]), ["owner"] = owner ?? "Document control",
                ["effectiveFrom"] = from, ["supersedes"] = new JsonArray([.. (supersedes ?? []).Select(s => (JsonNode)s)]),
            },
            ["truth"] = "",
        });
        File.WriteAllText(file, register.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The connectors for every Contoso source, reading this working copy.</summary>
    public IReadOnlyList<IConnector> Connectors(HttpClient intranet, HttpClient graph)
    {
        var register = Register();
        return
        [
            new FileShareConnector(Sources, "fileshare", register),
            new IntranetCrawler(intranet, register),
            new GraphDeltaConnector(graph, "contoso-sp", register),
            new MailboxConnector(Sources, register),
            new DatabaseConnector(Sources),
        ];
    }
}
