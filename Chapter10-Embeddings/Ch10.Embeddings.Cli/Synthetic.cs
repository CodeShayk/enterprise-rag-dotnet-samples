using System.Runtime.InteropServices;
using System.Text.Json;
using Contoso.Knowledge.Embeddings;

namespace Ch10.Embeddings.Cli;

/// <summary>One synthetic maintenance-log entry: the text that is embedded and the fields that filters use.</summary>
public sealed record LogEntry(int Id, string Site, string Tag, int Year, string Text);

/// <summary>
/// A synthetic maintenance log large enough for approximate search to matter: 100,000 entries about Contoso's pumps,
/// valves, gearboxes and drives at its plants and two customer sites, written from templates with a fixed seed, so
/// every run produces the same entries, vectors and queries. Corpus documents are too few (165 chunks) to show what
/// an index trades away.
/// </summary>
public static class Synthetic
{
    public static readonly string[] Sites = ["Riverside", "Northgate", "Lakeside", "Tailspin Water Utility", "Woodgrove Foods"];

    private static readonly (string Prefix, int From, int To, string Model, string[] Symptoms, string[] Actions)[] Equipment =
    [
        ("P-", 201, 299, "CP-200 pump",
            ["seal leak at the gland", "high vibration on the drive end", "bearing noise", "cavitation at low suction pressure", "low flow against the duty point",
             "seal-water pressure low, error E-217", "motor overheating", "coupling misalignment"],
            ["replaced the mechanical seal with PN-4471-C", "regreased both bearings", "realigned the coupling to 0.05 mm", "cleaned the suction strainer",
             "set the impeller clearance to 0.4 mm", "retorqued the M12 flange bolts to 85 N·m", "restored seal-water pressure to 2.5 bar"]),
        ("V-", 101, 199, "control valve",
            ["passing when closed", "stiff actuator", "packing leak", "position feedback fault", "slow stroke time"],
            ["repacked the gland", "replaced the seat ring", "recalibrated the positioner", "lubricated the stem", "replaced the feedback potentiometer"]),
        ("GBX-", 1, 40, "HG-400 gearbox",
            ["oil leak at the breather", "high oil temperature", "gear noise under load", "oil level low", "water in the oil"],
            ["changed the oil", "replaced the breather plug", "inspected the gear mesh", "topped up the oil", "replaced the shaft seal"]),
        ("VFD-", 1, 40, "VD-75 drive",
            ["fault F012 overcurrent", "fault F031 DC bus overvoltage", "cooling fan failure", "fault F007 earth leakage", "intermittent trips at start"],
            ["replaced the cooling fan", "reset the fault and checked the motor cable", "extended the ramp time", "replaced the braking resistor", "updated the firmware"]),
    ];

    private static readonly string[] Technicians = ["T. Silva", "K. Asante", "M. Okafor", "L. Novak", "R. Patel", "J. Moreau", "S. Lindqvist", "A. Haddad"];

    public static List<LogEntry> Entries(int count, int seed = 2027)
    {
        var random = new Random(seed);
        var entries = new List<LogEntry>(count);
        for (var id = 0; id < count; id++)
        {
            var (prefix, from, to, model, symptoms, actions) = Equipment[random.Next(Equipment.Length)];
            var tag = $"{prefix}{random.Next(from, to + 1).ToString(prefix == "P-" || prefix == "V-" ? "000" : "00")}";
            var site = Sites[random.Next(Sites.Length)];
            var date = new DateOnly(2019, 1, 1).AddDays(random.Next(365 * 8));
            var (symptom, action, who) = (symptoms[random.Next(symptoms.Length)], actions[random.Next(actions.Length)], Technicians[random.Next(Technicians.Length)]);
            var text = random.Next(3) switch
            {
                0 => $"{date:yyyy-MM-dd}, {site}: {tag} ({model}) reported {symptom}. {Capitalized(action)}. Technician {who}.",
                1 => $"Work order WO-{70000 + id} on {model} {tag} at {site}, {date:MMMM yyyy}: {symptom}; {action}.",
                _ => $"Inspection of {tag} at {site} on {date:d MMMM yyyy} found {symptom}. Action taken: {action}. Next check in {random.Next(2, 13)} weeks.",
            };
            entries.Add(new LogEntry(id, site, tag, date.Year, text));
        }

        return entries;
    }

    /// <summary>Questions a technician might ask of the log, from a different set of templates than the entries.</summary>
    public static List<string> Queries(int count, int seed = 31)
    {
        var random = new Random(seed);
        var queries = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var (prefix, from, to, model, symptoms, actions) = Equipment[random.Next(Equipment.Length)];
            var site = Sites[random.Next(Sites.Length)];
            var symptom = symptoms[random.Next(symptoms.Length)];
            queries.Add(random.Next(3) switch
            {
                0 => $"Which {model} units at {site} had {symptom}, and what fixed it?",
                1 => $"How was {symptom} resolved on a {model}?",
                _ => $"Has anyone at {site} {actions[random.Next(actions.Length)]} recently?",
            });
        }

        return queries;
    }

    private static string Capitalized(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    // ---- vectors on disk: float32, row after row, in .state/ch10 ----

    public static string Folder { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".state", "ch10"));

    public static void Save(string name, float[][] vectors)
    {
        Directory.CreateDirectory(Folder);
        using var file = File.Create(Path.Combine(Folder, name));
        foreach (var vector in vectors) file.Write(MemoryMarshal.AsBytes(vector.AsSpan()));
    }

    public static float[][] Load(string name, int dimensions = 384)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Folder, name));
        var vectors = new float[bytes.Length / (dimensions * sizeof(float))][];
        for (var i = 0; i < vectors.Length; i++)
        {
            vectors[i] = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(i * dimensions * sizeof(float), dimensions * sizeof(float))).ToArray();
        }

        return vectors;
    }

    /// <summary>Embeds texts as fast as this CPU allows: length-sorted batches of 32, one session shared by 14 callers.</summary>
    public static float[][] Embed(OnnxEmbeddingModel model, IReadOnlyList<string> texts, string prefix = "")
    {
        var order = Enumerable.Range(0, texts.Count).OrderBy(i => texts[i].Length).ToArray();
        var batches = order.Chunk(32).ToArray();
        var vectors = new float[texts.Count][];
        Parallel.ForEach(batches, new ParallelOptions { MaxDegreeOfParallelism = 14 }, batch =>
        {
            var embedded = model.Embed([.. batch.Select(i => prefix + texts[i])]);
            for (var j = 0; j < batch.Length; j++) vectors[batch[j]] = embedded[j];
        });
        return vectors;
    }

    public static void SaveEntries(IEnumerable<LogEntry> entries) =>
        File.WriteAllLines(Path.Combine(Folder, "logs.jsonl"), entries.Select(e => JsonSerializer.Serialize(e, JsonSerializerOptions.Web)));

    public static List<LogEntry> LoadEntries() =>
        [.. File.ReadLines(Path.Combine(Folder, "logs.jsonl")).Select(l => JsonSerializer.Deserialize<LogEntry>(l, JsonSerializerOptions.Web)!)];
}
