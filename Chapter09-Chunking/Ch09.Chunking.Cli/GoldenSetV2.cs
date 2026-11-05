using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Evaluation;

namespace Ch09.Chunking.Cli;

/// <summary>
/// Golden set v2, over the full corpus: version 1's questions ported to the new documents (their IDs and splits
/// kept, so Chapter 5's results stay comparable), questions generated from table rows by templates, and questions
/// written for the paragraphs and sections tables don't cover. Every span is the whole block or table row that holds
/// the answer, quoted from the document's canonical text projection, so the harness locates it exactly.
/// </summary>
public static partial class GoldenSetV2
{
    public static List<GoldenQuestion> Build(Corpus corpus, IReadOnlyList<GoldenQuestion> v1, out List<string> problems)
    {
        problems = [];
        var questions = new List<GoldenQuestion>();
        questions.AddRange(Port(corpus, v1, problems));
        questions.AddRange(FromTables(corpus));
        questions.AddRange(Written(corpus, problems));
        return questions;
    }

    // ---------- version 1, ported ----------

    private static readonly Dictionary<string, string> Moved = new(StringComparer.Ordinal)
    {
        ["CP200-TRB-E104"] = "CP200-TRB-2019", ["CP200-TRB-E217"] = "CP200-TRB-2019", ["CP200-TRB-VIB"] = "CP200-TRB-2019",
        ["HG400-MAN-3.2"] = "HG400-MAN-2021", ["HG400-MAN-4.1"] = "HG400-MAN-2021", ["VFD-MAN-F012"] = "VD75-MAN-2022",
        ["CP200-MAN-2009-5.4"] = "CP200-MAN-2009", ["CP200-MAN-2009-6.3"] = "CP200-MAN-2009", ["CP200-MAN-2009-7.2"] = "CP200-MAN-2009",
    };

    private static IEnumerable<GoldenQuestion> Port(Corpus corpus, IReadOnlyList<GoldenQuestion> v1, List<string> problems)
    {
        foreach (var q in v1)
        {
            var spans = new List<RelevantSpan>();
            foreach (var span in q.Relevant)
            {
                var doc = Moved.GetValueOrDefault(span.Doc, span.Doc);
                var text = corpus.Documents[doc].Text;
                var exact = text.Contains(span.Text, StringComparison.Ordinal) ? span.Text : Recover(text, span.Text);
                if (exact is null) problems.Add($"{q.Id}: span not found in {doc}: {span.Text}");
                else spans.Add(span with { Doc = doc, Text = exact });
            }

            if (spans.Count == q.Relevant.Count) yield return q with { Relevant = spans, Source = q.Source };
        }
    }

    /// <summary>The span as the new projection writes it: the same words, whatever the spacing or line breaks.</summary>
    private static string? Recover(string text, string span)
    {
        var pattern = string.Join(@"\s+", span.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Value : null;
    }

    // ---------- generated from table rows ----------

    private sealed record TableTemplate(string Doc, string FirstHeader, int Every, string Type, Func<IReadOnlyList<string>, string> Question, string? Asker = null);

    private static readonly TableTemplate[] Templates =
    [
        new("CP200-TRB-2019", "Code", 3, "lookup", r => $"What does error {r[0]} mean on a CP-200, and does it trip the pump?"),
        new("CP200-MAN-2009", "Item", 2, "numeric", r => $"What is the {Lower(r[0])} of a CP-200 pump?"),
        new("CP200-MAN-2009", "Task", 2, "numeric", r => $"How often should you {Lower(Plain(r[0]))} on a CP-200?"),
        new("CP200-MAN-2009", "Part", 2, "lookup", r => $"What is the part number of the CP-200's {Lower(r[0])}, and how many should a site keep?"),
        new("CP200-MAN-2009", "Symptom", 2, "lookup", r => $"A CP-200 has this problem: {Lower(r[0])}. What is the likely cause and the remedy?"),
        new("HG400-MAN-2021", "Ratio", 2, "numeric", r => $"What output torque and thermal rating does an HG-400 with a {r[0]} to 1 ratio have?"),
        new("HG400-MAN-2021", "Oil type", 1, "numeric", r => $"How often should {Lower(r[0])} be changed in an HG-400?"),
        new("HG400-MAN-2021", "Task", 2, "numeric", r => $"How often should you {Lower(Plain(r[0]))} on an HG-400 gearbox?"),
        new("HG400-MAN-2021", "Symptom", 2, "lookup", r => $"An HG-400 gearbox has this problem: {Lower(r[0])}. What could cause it?"),
        new("VD75-MAN-2022", "Parameter", 3, "numeric", r => $"What are the default and the range of VD-75 parameter {r[0]}, {Lower(r[1])}?"),
        new("VD75-MAN-2022", "Code", 3, "lookup", r => $"What does fault {r[0]} on a VD-75 drive mean, and how do I fix it?"),
        new("CPL-RX-INST", "Misalignment", 1, "numeric", r => $"What is the permissible {Lower(r[0])} misalignment for an RX coupling?"),
        new("CPL-RX-INST", "Size", 2, "numeric", r => $"What is the maximum torque of an RX coupling of size {r[0]}?"),
        new("PARTS-CAT-2027", "Part", 4, "numeric", r => $"What are the lead time and the net price of {r[0]}?", "grace.liu"),
        new("PARTS-CAT-2027", "Change", 1, "lookup", r => $"What replaced {r[0]}, and under which change notice?"),
        new("CMMS-WORK-ORDERS", "work_order", 6, "lookup", r => $"What was work order {r[0]} for, and when was it completed?", "nadia.rahman"),
        new("SC-2024-118", "Part number", 1, "numeric", r => $"What are the unit price and the minimum order of {r[0]} under the Fabrikam Seals contract?"),
        new("PROC-POL-007", "Order value", 1, "lookup", r => $"Who has to approve a purchase order of {Lower(r[0])}?"),
        new("SVC-POL-002", "Priority", 1, "numeric", r => $"How soon must Field Services be on site for a {r[0]} call?", "kwame.asante"),
    ];

    /// <summary>Key-value records (asset records, change-notice headers): one written question per chosen field.</summary>
    private static readonly (string Doc, string Field, string Question, string Type)[] Fields =
    [
        ("P201-ASSET", "Site", "Where is pump P-201 installed?", "lookup"),
        ("P201-ASSET", "Motor", "What motor drives pump P-201?", "numeric"),
        ("P201-ASSET", "Commissioned", "When was pump P-201 commissioned?", "temporal"),
        ("P202-ASSET", "Duty", "What duty does pump P-202 serve?", "lookup"),
        ("P202-ASSET", "Seal-water valve", "Which valve supplies seal water to pump P-202?", "lookup"),
        ("P202-ASSET", "Mechanical seal", "Which mechanical seal is fitted to pump P-202?", "lookup"),
        ("P301-ASSET", "Site", "Where is pump P-301 installed?", "lookup"),
        ("P301-ASSET", "Seal-water valve", "Which valve supplies seal water to pump P-301?", "lookup"),
        ("P301-ASSET", "Commissioned", "When was pump P-301 commissioned?", "temporal"),
        ("ECN-2291", "Date raised", "When was change notice ECN-2291 raised?", "temporal"),
        ("ECN-2240", "Originator", "Who originated change notice ECN-2240?", "lookup"),
        ("ECN-2240", "Effective", "When did change notice ECN-2240 take effect?", "temporal"),
    ];

    private static IEnumerable<GoldenQuestion> FromTables(Corpus corpus)
    {
        var n = 0;
        foreach (var (doc, field, question, type) in Fields)
        {
            var line = corpus.Sources.Single(s => s.Id == doc).Text.Split('\n').Single(l => l.StartsWith($"| {field} | ", StringComparison.Ordinal));
            yield return Question($"G2-F{++n:000}", null, question, type, [new RelevantSpan(doc, line)], corpus);
        }

        n = 0;
        foreach (var template in Templates)
        {
            var source = corpus.Sources.Single(s => s.Id == template.Doc);
            foreach (var table in source.Document.Blocks.OfType<TableBlock>().Where(t => t.Role is null && t.Rows.Count > 0 && t.Rows[0][0] == template.FirstHeader))
            {
                var lines = table.ToMarkdown().Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).ToList();
                var rows = table.Rows.Skip(Math.Max(table.HeaderRows, 1)).ToList();
                var data = lines.Skip(lines.Count - rows.Count).ToList();
                for (var i = 0; i < rows.Count; i += template.Every)
                {
                    // A row whose answer points elsewhere ("As Table 4-1") can't answer its question alone.
                    if (rows[i].Count == 0 || string.IsNullOrWhiteSpace(rows[i][0]) || rows[i].Any(c => c.StartsWith("As ", StringComparison.Ordinal))) continue;
                    var span = new RelevantSpan(template.Doc, data[i]);
                    yield return Question($"G2-T{++n:000}", template.Asker, template.Question(rows[i]), template.Type, [span], corpus);
                }
            }
        }
    }

    // ---------- written for paragraphs, list items and sections ----------

    private sealed record Written2(string Question, string Type, string Doc, string Phrase, string? Asker = null);

    private static readonly Written2[] Hand =
    [
        new("How do I level the baseplate when installing a CP-200?", "numeric", "CP200-MAN-2009", "Level the baseplate to within 0.2 mm per meter"),
        new("Can the flanges of a CP-200 carry the weight of the pipework?", "lookup", "CP200-MAN-2009", "the pump flanges must carry no pipe loads"),
        new("What has to be fitted in the seal-water line of a CP-200?", "lookup", "CP200-MAN-2009", "Fit an isolation valve, a strainer and a pressure gauge in the line."),
        new("What clearance should there be between a CP-200 impeller and the casing wear ring?", "numeric", "CP200-MAN-2009", "Set the clearance between the impeller and the casing wear ring"),
        new("What torque do the seal housing bolts on a CP-200 need?", "numeric", "CP200-MAN-2009", "Tighten the four seal housing bolts to 45 N·m."),
        new("Which grease do the CP-200 bearings take?", "lookup", "CP200-MAN-2009", "Use lithium-complex grease of NLGI grade 2"),
        new("Does Contoso stock seal faces for the CP-200 separately?", "lookup", "CP200-MAN-2009", "Contoso doesn't stock seal faces separately."),
        new("What should I check during the first hour after starting a CP-200?", "numeric", "CP200-MAN-2009", "During the first hour, check that the bearing temperatures stay below 80 °C"),
        new("When should I drain the casing after shutting down a CP-200?", "procedural", "CP200-MAN-2009", "Drain the casing if the pump may be exposed to frost."),
        new("What should I do before loosening the impeller nut on a CP-200?", "procedural", "CP200-MAN-2009", "Mark the position of the impeller nut before loosening it."),
        new("What should I do after error E-221 before resetting it?", "procedural", "CP200-TRB-2019", "Let the bearings cool, check the alignment and the amount of grease, then reset E-221."),
        new("At what vibration levels does the CP-200 controller warn and trip?", "numeric", "CP200-TRB-2019", "The controller warns at 4.5 mm/s RMS and trips the pump at 7.1 mm/s RMS."),
        new("What does cavitation in a pump sound like?", "lookup", "CP200-TRB-2019", "Cavitation is the collapse of vapor bubbles"),
        new("Is it normal for a new mechanical seal to drip?", "lookup", "CP200-TRB-2019", "A new mechanical seal may drip for the first few hours"),
        new("Which battery keeps the CP-200 controller's clock running?", "lookup", "CP200-TRB-2019", "Replace the CR2032 battery behind the controller's front panel."),
        new("What does a running CP-200 do when it loses contact with the plant control system?", "lookup", "CP200-TRB-2019", "The pump keeps running at its last set point."),
        new("In what order is a pump's isolation removed after the work?", "procedural", "SOP-FS-007", "Remove the isolation in the reverse order once everyone has removed their lock."),
        new("Which valve feeds seal water to pump P-201?", "lookup", "SOP-FS-007", "valve V-114 feeds pump P-201"),
        new("How do I prove that an isolated pump can't start?", "procedural", "SOP-FS-007", "Try to start the pump from the local panel to prove that it can't start"),
        new("What happens to the old seal after a CP-200 seal replacement?", "procedural", "SOP-FS-014", "Record the seal's part number, the pump's operating hours"),
        new("What torque wrench do I need for a CP-200 seal replacement?", "numeric", "SOP-FS-014", "Torque wrench covering 5 to 60 N·m."),
        new("What size of screen is in the strainer upstream of V-114?", "numeric", "WI-FS-031", "Y-strainer with a 100-micron screen"),
        new("How much oil does an HG-400 take at an oil change, and which grade?", "numeric", "SOP-MS-003", "18 liters for an HG-400", "kwame.asante"),
        new("Why does gearbox GBX-07 have a 10,000-hour oil change interval?", "lookup", "SOP-MS-003", "Gearbox GBX-07 on the Lakeside packaging line runs on synthetic PAO oil", "kwame.asante"),
        new("What seal-water pressure should the flush connection show when a CP-200 restarts after a seal change?", "numeric", "TRN-FS-2026-04", "Open the seal-water supply: 2.0 to 3.0 bar at the flush connection"),
        new("How should an HG-400 be stored for more than six months?", "procedural", "HG400-MAN-2021", "For storage longer than six months, fill it completely with oil"),
        new("How flat must the mounting surface for an HG-400 be?", "numeric", "HG400-MAN-2021", "the mounting surface must be flat to within 0.1 mm"),
        new("How should couplings be fitted to the shafts of an HG-400?", "procedural", "HG400-MAN-2021", "Fit couplings and sprockets by heating them to about 100 °C"),
        new("What has to be done with the HG-400's transport plug before filling it with oil?", "procedural", "HG400-MAN-2021", "The gearbox is shipped with a closed transport plug in the breather position."),
        new("How long should a new HG-400 run at no more than half load?", "numeric", "HG400-MAN-2021", "Run a new gearbox at no more than half load for its first 50 hours."),
        new("Why would a VD-75 drive stop with fault F010?", "lookup", "VD75-MAN-2022", "Fault F010 means the speed reference on a 4 to 20 mA input fell below 2 mA"),
        new("How much free space does a VD-75 drive need above and below it?", "numeric", "VD75-MAN-2022", "Mount the drive vertically with 100 mm of free space above and below it for cooling air."),
        new("When should the alignment of an RX coupling be checked again?", "numeric", "CPL-RX-INST", "Re-check the alignment after the first 100 hours of operation"),
        new("What did ECN-2240 change on the CP-200, and from when?", "temporal", "ECN-2240", "casing wear ring PN-2840-A (bronze) is superseded by PN-2840-B", "nadia.rahman"),
        new("Do I need a permit to weld outside the workshops?", "lookup", "SB-0947", "Welding, grinding and cutting outside the workshops need a hot work permit"),
        new("How long is a hot work permit valid?", "temporal", "SB-0947", "The permit is valid for one shift."),
        new("What oxygen level must a gas test show before someone enters a confined space?", "numeric", "SB-0901", "a gas test that shows at least 19.5 percent oxygen"),
        new("When do I need a platform instead of a ladder in a pump gallery?", "numeric", "SB-0915", "Use a mobile elevating work platform or a scaffold with guardrails for work more than 2 meters above the floor"),
        new("What protection do I need to descale strainer screens with citric acid?", "lookup", "SB-0950", "Wear chemical goggles and nitrile gloves"),
        new("Whom does a Lakeside technician call when a seal-water line can't be isolated?", "lookup", "SB-0932-LAKESIDE", "At the Lakeside plant, report any seal-water line"),
        new("Can I paste a Contoso drawing into a public AI chatbot?", "lookup", "IT-POL-003", "Don't paste Contoso documents, drawings, customer data or personal data into public AI chatbots"),
        new("Can I split a purchase order to stay under an approval limit?", "lookup", "PROC-POL-007", "Splitting an order to stay under an approval limit is a breach of this policy.", "grace.liu"),
        new("When must a P1 call be escalated?", "temporal", "SVC-POL-002", "Escalate any P1 call that has no engineer assigned after 30 minutes", "kwame.asante"),
        new("How far in advance must employees agree the start of parental leave with their manager?", "temporal", "HR-PL-2024", "Employees agree the start date with their manager at least eight weeks in advance.", "daniel.mensah"),
        new("Do employees on fixed-term contracts get the new parental leave?", "lookup", "HR-PL-2027", "Employees on fixed-term contracts receive the same leave", "daniel.mensah"),
        new("What credit does Contoso get when Fabrikam Seals delivers late?", "numeric", "SC-2024-118", "Late deliveries earn the buyer a credit of 1 percent of the order value", "grace.liu"),
        new("How long is the warranty on seals from Fabrikam Seals?", "numeric", "SC-2024-118", "The supplier warrants each seal against defects in material and workmanship", "grace.liu"),
        new("How much notice stops the Fabrikam Seals contract from renewing?", "temporal", "SC-2024-118", "renews for one year unless either party gives notice 90 days before it ends", "grace.liu"),
        new("What was wrong with the CP-200 at Tailspin's treated-water pumping station 1?", "lookup", "SR-2026-1022", "Findings: the impeller clearance had grown to 1.4 mm.", "kwame.asante"),
        new("What caused the F002 faults at the Woodgrove Foods cold store?", "lookup", "SR-2026-1130", "Findings: the ramp-down time had been set to 1 second after a controller upgrade", "kwame.asante"),
        new("What seal-water pressure did the January follow-up visit at pumping station 3 find?", "numeric", "SR-2027-0112", "Seal-water pressure was 2.6 bar.", "kwame.asante"),
        new("What price per seal should Tailspin be quoted for four PN-4471-C seals?", "numeric", "MAIL-2027-0120-SEAL-KITS", "our agreement with Fabrikam gives us 1,340 USD per seal", "kwame.asante"),
        new("Are the results of the oil analysis on the Woodgrove HG-400 back yet?", "temporal", "SR-2027-0119", "oil sample from the HG-400 taken for analysis after the December oil change; results pending.", "kwame.asante"),
    ];

    private static IEnumerable<GoldenQuestion> Written(Corpus corpus, List<string> problems)
    {
        var n = 0;
        foreach (var w in Hand)
        {
            n++;
            var source = corpus.Sources.Single(s => s.Id == w.Doc);
            var at = source.Text.IndexOf(w.Phrase, StringComparison.Ordinal);
            if (at < 0)
            {
                problems.Add($"G2-H{n:000}: phrase not found in {w.Doc}: {w.Phrase}");
                continue;
            }

            // The span is the whole block that holds the phrase, or its line when the block is a table.
            var block = source.Projection.BlockAt(at)!;
            var (start, end) = (block.Start, block.End);
            if (source.Document.Blocks[block.Index] is TableBlock)
            {
                start = source.Text.LastIndexOf('\n', at) + 1;
                end = source.Text.IndexOf('\n', at) is var e and >= 0 && e < block.End ? e : block.End;
            }

            yield return Question($"G2-H{n:000}", w.Asker, w.Question, w.Type, [new RelevantSpan(w.Doc, source.Text[start..end])], corpus);
        }
    }

    // ---------- shared ----------

    private static GoldenQuestion Question(string id, string? asker, string text, string type, IReadOnlyList<RelevantSpan> spans, Corpus corpus)
    {
        var documents = spans.Select(s => corpus.Documents[s.Doc]).ToList();
        string[] preferred = documents[0].Tenant switch
        {
            "fluid" => ["tomas.silva", "nadia.rahman"],
            "motion" or "service" => ["kwame.asante"],
            _ => ["tomas.silva", "grace.liu", "daniel.mensah", "riley.chen"],
        };
        var reader = (asker is null ? preferred : [asker]).Concat(People.All.Select(p => p.Id))
            .FirstOrDefault(id => documents.All(d => People.Find(id)!.CanRead(d)))
            ?? throw new InvalidDataException($"{id}: nobody can read {string.Join(", ", documents.Select(d => $"{d.Id} ({string.Join("/", d.AllowedGroups)})"))}");
        var split = SHA256.HashData(Encoding.UTF8.GetBytes(id))[0] % 5 < 3 ? "dev" : "test";
        return new GoldenQuestion(id, reader, text, type, "generated", split, spans);
    }

    private static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static string Plain(string s) => Parenthesis().Replace(s, "");

    [GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial Regex Parenthesis();
}
