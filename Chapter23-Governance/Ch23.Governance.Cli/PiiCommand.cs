using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Governance;
using Contoso.Knowledge.Observability;

namespace Ch23.Governance.Cli;

/// <summary>
/// Personal data in the corpus as ingestion sees it: every block of each parsed document, quoted replies included,
/// scanned with patterns for email addresses and phone numbers and the NER model for people's names. Then how much of
/// it reaches the index (the text projection leaves quotes out) and what Chapter 20's telemetry redactor, which knows
/// only Contoso's own people, would leave in place.
/// </summary>
public static class PiiCommand
{
    public static async Task RunAsync(Lab lab)
    {
        using var ner = Lab.Ner();
        var scanner = new PiiScanner(text => ner.Find(text).Where(e => e.Label == "PER" && e.Score >= 0.9).Select(e => e.Text));
        var known = People.All.SelectMany(p => new[] { p.Name, p.Name.Split(' ')[0] }).ToHashSet(StringComparer.Ordinal);
        Console.WriteLine($"{lab.Documents.Count} parsed documents, every block: patterns for addresses and phone numbers, bert-base-NER for names (score >= 0.9)");
        Console.WriteLine($"{"Document type",-16} {"documents",9} {"with PII",9} {"emails",7} {"phones",7} {"names",6} {"in the index",13}  names outside Contoso's directory");
        var (missed, total, indexed) = (new List<string>(), 0, 0);
        foreach (var group in lab.Entries.GroupBy(e => lab.Chunks.First(c => c.Document.Id == e.Key).DocumentType).OrderBy(g => g.Key))
        {
            var rows = new List<(string Id, IReadOnlyList<PiiFinding> Found, int Indexed)>();
            foreach (var (id, entry) in group)
            {
                var full = (await GeneratedCorpus.TruthAsync(entry)).ToText(new HashSet<string>()).Text;
                var found = scanner.Scan(full);
                rows.Add((id, found, found.Count(f => lab.Documents[id].Text.Contains(f.Text, StringComparison.Ordinal))));
                var redacted = TelemetryContent.Redact(full);
                missed.AddRange(found.Where(f => f.Kind == "person" && redacted.Contains(f.Text, StringComparison.Ordinal)).Select(f => $"{f.Text} ({id})"));
            }

            var names = rows.SelectMany(r => r.Found.Where(f => f.Kind == "person").Select(f => f.Text)).Distinct().ToList();
            (total, indexed) = (total + rows.Sum(r => r.Found.Count), indexed + rows.Sum(r => r.Indexed));
            Console.WriteLine($"{group.Key,-16} {rows.Count,9} {rows.Count(r => r.Found.Count > 0),9} {rows.Sum(r => r.Found.Count(f => f.Kind == "email")),7} " +
                $"{rows.Sum(r => r.Found.Count(f => f.Kind == "phone")),7} {names.Count,6} {rows.Sum(r => r.Indexed),13}  {string.Join(", ", names.Where(n => !known.Contains(n)))}");
        }

        Console.WriteLine($"findings: {total}, of which {indexed} reach the index text");
        Console.WriteLine($"names Chapter 20's redactor leaves in place: {string.Join(", ", missed.Distinct())}");
    }
}
