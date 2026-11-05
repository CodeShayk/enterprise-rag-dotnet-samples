using System.Globalization;
using Contoso.Corpus.Generator.Writers;
using Contoso.Knowledge.Documents;
using static Contoso.Corpus.Generator.Content.Meta;

namespace Contoso.Corpus.Generator.Content;

/// <summary>Field Services: service reports and policy on SharePoint, the service mailbox and the work-order export.</summary>
internal static class Service
{
    private static readonly string[] ServiceOnly = ["service"];

    public static IEnumerable<SourceSpec> All()
    {
        foreach (var report in Reports()) yield return report;
        yield return ResponsePolicy();
        foreach (var mail in Mailbox()) yield return mail;
        yield return WorkOrders();
    }

    private static readonly (string Id, string Seed, string Customer, string Site, string Equipment, DateOnly Date, string[] Paragraphs)[] ReportData =
    [
        ("SR-2026-1107", "sr-2026-1107", "Tailspin Water Utility", "Raw-water pumping station 3", "CP-200 pump", D(2026, 11, 7), []),
        ("SR-2026-1213", "sr-2026-1213", "Woodgrove Foods", "Packaging line 2", "HG-400 gearbox", D(2026, 12, 13), []),
        ("SR-2026-1022", "", "Tailspin Water Utility", "Treated-water pumping station 1", "CP-200 pump", D(2026, 10, 22),
        [
            "Complaint: the pump delivered about 20 percent less water than its commissioning test and drew less current.",
            "Findings: the impeller clearance had grown to 1.4 mm. The bronze wear ring was worn unevenly, which points to the shaft running out of true.",
            "Work done: fitted a new wear ring PN-2840-B under ECN-2240 and set the clearance to 0.25 mm. Delivery returned to the commissioning value. Recommended a vibration survey at the next visit.",
        ]),
        ("SR-2026-1130", "", "Woodgrove Foods", "Cold store 1", "VD-75 drive", D(2026, 11, 30),
        [
            "Complaint: the evaporator fan drive stopped several times a night with fault F002.",
            "Findings: the ramp-down time had been set to 1 second after a controller upgrade, too short for the fan's inertia.",
            "Work done: raised parameter 2-11 (ramp-down time) to 20 seconds. No further F002 faults in 48 hours of logging.",
        ]),
        ("SR-2027-0112", "", "Tailspin Water Utility", "Raw-water pumping station 3", "CP-200 pump", D(2027, 1, 12),
        [
            "Complaint: follow-up visit after SR-2026-1107 to fit the duplex strainer the customer ordered.",
            "Findings: the mechanical seal PN-4471-C fitted in November showed no leakage. Seal-water pressure was 2.6 bar.",
            "Work done: fitted a duplex strainer with 100-micron screens upstream of the seal-water valve and showed the customer's operators how to switch between screens without stopping the pump.",
        ]),
    ];

    private static IEnumerable<SourceSpec> Reports()
    {
        foreach (var r in ReportData)
        {
            var b = new DocBuilder(r.Id, "1", Of($"Field service report {r.Id}", "service", DocumentTypes.ServiceReport,
                SensitivityLabels.Internal, ServiceOnly, "Kwame Asante", from: r.Date,
                tags: [r.Equipment.Split(' ')[0]], parts: [.. new[] { "PN-4471-B", "PN-4471-C", "PN-2840-B" }.Where(p => r.Paragraphs.Concat(r.Seed.Length > 0 ? [SeedText.Document(r.Seed).Text] : []).Any(t => t.Contains(p)))]));
            if (r.Seed.Length > 0)
            {
                b.Seed(r.Seed);
            }
            else
            {
                b.H(1, $"Field service report {r.Id}");
                b.P($"Customer: {r.Customer}, {char.ToLowerInvariant(r.Site[0])}{r.Site[1..]}. Engineer: Kwame Asante. Date: {r.Date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}.");
                foreach (var p in r.Paragraphs) b.P(p);
            }

            b.Table(null, ["Equipment", "Site", "Report"], [r.Equipment, $"{r.Customer}, {r.Site}", r.Id]);
            yield return new SourceSpec(b.Build("sharepoint", $"sharepoint/service/reports/{r.Id}.pdf"), Format.Pdf, new DateTimeOffset(r.Date.ToDateTime(new TimeOnly(18, 0)), TimeSpan.Zero))
            {
                Author = "Kwame Asante",
                Page = PageStyle.Default with { RunningHeader = "Contoso Industrial · Field Services · Service report", RunningFooter = "{page}/{pages}" },
            };
        }

        // A draft uploaded without the library's required columns: the data contract quarantines it.
        var draft = new DocBuilder("SR-2027-0119", "draft", Of("Field service report SR-2027-0119 (draft)", "service", DocumentTypes.ServiceReport,
            SensitivityLabels.Internal, ServiceOnly, "Kwame Asante", from: D(2027, 1, 19)));
        draft.H(1, "Field service report SR-2027-0119 (draft)")
             .P("Customer: Woodgrove Foods, packaging line 2. Engineer: Kwame Asante. Date: January 19, 2027.")
             .P("Notes so far: oil sample from the HG-400 taken for analysis after the December oil change; results pending.");
        yield return new SourceSpec(draft.Build("sharepoint", "sharepoint/service/reports/SR-2027-0119 draft.docx"), Format.Docx, new DateTimeOffset(2027, 1, 19, 20, 15, 0, TimeSpan.Zero))
        {
            Author = "Kwame Asante",
            MissingMetadata = ["sensitivity", "owner"],
            Note = "Uploaded without the sensitivity and owner columns the library requires.",
        };
    }

    private static SourceSpec ResponsePolicy()
    {
        var b = new DocBuilder("SVC-POL-002", "3", Of("SVC-POL-002: Response times for contracted customers", "service", DocumentTypes.Policy,
            SensitivityLabels.Internal, ServiceOnly, "Field Services management", revision: "3", from: D(2025, 4, 1)));
        b.Seed("svc-pol-002")
         .P("Response times are measured from the time the call is logged to the time the engineer arrives on site. Calls logged outside working hours count from 07:00 the next business day, except P1 calls, which count from the time they are logged.");
        return new SourceSpec(b.Build("sharepoint", "sharepoint/service/SVC-POL-002 Response times.docx"), Format.Docx, new DateTimeOffset(2025, 3, 20, 9, 0, 0, TimeSpan.Zero))
        {
            Author = "Field Services management",
        };
    }

    private static IEnumerable<SourceSpec> Mailbox()
    {
        const string kwame = "Kwame Asante <kwame.asante@contoso.example>";
        const string pat = "Pat Lee <pat.lee@tailspin.example>";
        const string sam = "Sam Okafor <sam.okafor@woodgrove.example>";
        const string grace = "Grace Liu <grace.liu@contoso.example>";
        static DateTimeOffset At(int y, int m, int d, int h, int min) => new(y, m, d, h, min, 0, TimeSpan.Zero);

        MailMessageSpec[] tailspin =
        [
            new(pat, kwame, At(2026, 11, 5, 8, 12), "Pumping station 3: CP-200 tripping on E-217",
                ["Kwame, the CP-200 at raw-water pumping station 3 has tripped four times since yesterday with error E-217. We reset it each time, but it trips again within a few hours. Can someone come out this week?", "Pat Lee, maintenance lead, Tailspin Water Utility"]),
            new(kwame, pat, At(2026, 11, 5, 9, 40), "RE: Pumping station 3: CP-200 tripping on E-217",
                ["Hi Pat, I can be on site Saturday morning, November 7.", "Until then, check that the seal-water valve is fully open and look at the strainer upstream of it. Sand from the raw-water supply blocks that strainer quickly, and a blocked strainer is the most common cause of E-217.", "Kwame"]),
            new(kwame, pat, At(2026, 11, 7, 18, 5), "RE: Pumping station 3: CP-200 tripping on E-217",
                ["Hi Pat, the report from today's visit is attached.", "In short: the seal-water strainer was almost fully blocked with sand, and the mechanical seal already showed face wear, so we replaced it with PN-4471-C under ECN-2291. I'd recommend a duplex strainer so your operators can clean one screen while the pump keeps running. I'll send a quote.", "Kwame"]) { Cc = "Field Services dispatch <dispatch@contoso.example>" },
        ];
        yield return Mail("mail/service/2026-11-07 Pumping station 3 E-217 trips.eml", tailspin, ["sharepoint/service/reports/SR-2026-1107.pdf"],
            "Thread with quoted replies and the service report attached (the same bytes as the report on SharePoint).");

        MailMessageSpec[] woodgrove =
        [
            new(sam, kwame, At(2026, 12, 11, 7, 55), "Line 2 gearbox noise",
                ["Hello Kwame, the gearbox on packaging line 2 has started whining, and the noise gets louder as the line speeds up. Could you take a look before our holiday production run?", "Sam Okafor, engineering, Woodgrove Foods"]),
            new(kwame, sam, At(2026, 12, 14, 17, 30), "RE: Line 2 gearbox noise",
                ["Hello Sam, report attached. The oil level was below the sight glass and the oil was 14 months old, so we flushed and refilled the gearbox and replaced a leaking output shaft seal. The first-stage gears show light pitting; please send an oil sample every six months so we can watch it.", "Kwame"]),
        ];
        yield return Mail("mail/service/2026-12-14 Line 2 gearbox noise.eml", woodgrove, ["sharepoint/service/reports/SR-2026-1213.pdf"], null);

        MailMessageSpec[] quote =
        [
            new(kwame, grace, At(2027, 1, 20, 10, 2), "Seal kits for Woodgrove and Tailspin",
                ["Grace, two customers want spare seal kits for their CP-200 pumps: Woodgrove Foods two and Tailspin Water Utility four. Can I quote PN-4471-C at the catalog price, or is there a volume discount from Fabrikam Seals?"]),
            new(grace, kwame, At(2027, 1, 20, 14, 47), "RE: Seal kits for Woodgrove and Tailspin",
                ["Kwame, quote the catalog price for Woodgrove. For Tailspin's four, our agreement with Fabrikam gives us 1,340 USD per seal at a minimum order of four, so we can offer them 8 percent below the catalog price.", "Make sure nobody quotes PN-4471-B any more; the remaining stock is quarantined under ECN-2291.", "Grace"]),
        ];
        yield return Mail("mail/procurement/2027-01-20 Seal kits for customers.eml", quote, [], null) with
        {
            Truth = MailTruth("MAIL-2027-0120-SEAL-KITS", "Seal kits for Woodgrove and Tailspin", "mail/procurement/2027-01-20 Seal kits for customers.eml", ["service", "procurement"], D(2027, 1, 20)),
        };
    }

    private static SourceSpec Mail(string path, MailMessageSpec[] thread, string[] attachments, string? note)
    {
        var newest = thread[^1];
        var id = "MAIL-" + newest.Date.ToString("yyyy-MMdd", CultureInfo.InvariantCulture) + "-" +
            new string([.. newest.Subject.Replace("RE: ", "").Where(char.IsLetterOrDigit).Take(10)]).ToUpperInvariant();
        return new SourceSpec(MailTruth(id, newest.Subject.Replace("RE: ", ""), path, ServiceOnly, DateOnly.FromDateTime(newest.Date.Date)), Format.Eml, newest.Date)
        {
            Thread = thread,
            Attachments = attachments,
            Note = note,
        };
    }

    private static CanonicalDocument MailTruth(string id, string subject, string path, string[] groups, DateOnly date) =>
        new DocBuilder(id, "1", Of(subject, "service", DocumentTypes.Email, SensitivityLabels.Internal, groups, "Field Services", from: date))
            .Build("mail", path);

    private static SourceSpec WorkOrders()
    {
        var rows = WorkOrderData.Rows();
        var b = new DocBuilder("CMMS-WORK-ORDERS", "2027-02-01", Of("Work orders, July 2026 to January 2027 (export)", "service", DocumentTypes.WorkOrders,
            SensitivityLabels.Internal, ["fluid", "motion", "service"], "Maintenance systems", from: D(2027, 2, 1)));
        b.Add(new TableBlock([WorkOrderData.Header, .. rows]));
        return new SourceSpec(b.Build("database", "database/work-orders.csv"), Format.Csv, new DateTimeOffset(2027, 2, 1, 2, 0, 0, TimeSpan.Zero))
        {
            Csv = (WorkOrderData.Header, rows),
            Note = "Nightly export of the maintenance system's work orders, in technicians' shorthand.",
        };
    }
}

/// <summary>Work orders for Contoso's assets, July 2026 to January 2027, in the shorthand technicians type.</summary>
internal static class WorkOrderData
{
    public static IReadOnlyList<string> Header { get; } =
        ["work_order", "asset_tag", "site", "type", "priority", "description", "status", "opened", "due", "completed", "technician", "updated_at"];

    private static readonly (string Tag, string Site, string Pm, int EveryDays)[] Assets =
    [
        ("P-201", "Riverside", "PM 2000h insp mech seal + brgs", 60),
        ("P-202", "Riverside", "PM 2000h insp mech seal + brgs (standby)", 90),
        ("V-114", "Riverside", "PM chk SW valve + Y-strainer 100um", 45),
        ("P-301", "Northgate", "PM 2000h insp mech seal + brgs", 75),
        ("GBX-07", "Lakeside", "PM oil lvl + leaks wk chk, sample 6m", 30),
        ("HG-1102", "Eastfield", "PM oil lvl + leaks, clean fins", 45),
        ("CNV-12", "Lakeside", "PM VD-75 fan + filters", 60),
    ];

    private static readonly string[] Technicians = ["tsilva", "mokoro", "jhansen", "lperez", "aking"];

    public static IReadOnlyList<IReadOnlyList<string>> Rows()
    {
        var random = new Random(2027);
        var rows = new List<string[]>();
        foreach (var (tag, site, pm, every) in Assets)
        {
            for (var due = new DateOnly(2026, 7, 6).AddDays(random.Next(0, every)); due < new DateOnly(2027, 2, 1); due = due.AddDays(every))
            {
                var opened = due.AddDays(-14);
                var late = random.NextDouble() < 0.22 ? random.Next(3, 25) : -random.Next(0, 6);
                var completedOn = due.AddDays(late);
                var done = completedOn < new DateOnly(2027, 1, 29);
                var technician = Technicians[random.Next(Technicians.Length)];
                var outcome = done ? (random.NextDouble() < 0.8 ? ", ok" : ", see notes") : "";
                rows.Add(["", tag, site, "PM", "P3", pm + outcome, done ? "closed" : "open", $"{opened:yyyy-MM-dd}", $"{due:yyyy-MM-dd}",
                    done ? $"{completedOn:yyyy-MM-dd}" : "", technician, done ? $"{completedOn:yyyy-MM-dd}T16:00:00Z" : $"{opened:yyyy-MM-dd}T08:00:00Z"]);
            }
        }

        // Corrective work that other documents refer to.
        (string Tag, string Site, string Priority, string Text, string Opened, string Due, string Completed, string Tech)[] corrective =
        [
            ("P-201", "Riverside", "P2", "E217 trip x2 nights - Y-strainer blocked, cleaned per WI-FS-031, SW 2.6 bar", "2026-08-18", "2026-08-19", "2026-08-19", "tsilva"),
            ("P-201", "Riverside", "P2", "chg mech seal PN-4471-B -> PN-4471-C per ECN-2291, LOTO per SOP-FS-007, gland nuts 25Nm", "2026-11-16", "2026-11-20", "2026-11-19", "tsilva"),
            ("P-202", "Riverside", "P3", "seal drip ~10/h, monitor; chg to PN-4471-C at next seal change", "2026-10-02", "2026-10-30", "", "mokoro"),
            ("P-301", "Northgate", "P2", "chg mech seal to PN-4471-C per ECN-2291", "2026-12-01", "2026-12-04", "2026-12-03", "jhansen"),
            ("GBX-07", "Lakeside", "P2", "oil leak output shaft - new seal, topped up PAO VG220 2l", "2026-09-09", "2026-09-11", "2026-09-10", "lperez"),
            ("CNV-12", "Lakeside", "P1", "VD-75 F012 at start, ramp-up 2-10 raised 2s -> 5s, ok", "2026-10-14", "2026-10-14", "2026-10-14", "aking"),
        ];
        foreach (var c in corrective)
        {
            rows.Add(["", c.Tag, c.Site, "CM", c.Priority, c.Text, c.Completed.Length > 0 ? "closed" : "open", c.Opened, c.Due, c.Completed, c.Tech,
                (c.Completed.Length > 0 ? c.Completed : c.Opened) + "T16:00:00Z"]);
        }

        // Numbers grow with the opened date, about 2.8 a day across all of Contoso's assets (most aren't in this export).
        var used = new HashSet<int>();
        var ordered = rows.OrderBy(r => r[7], StringComparer.Ordinal).ThenBy(r => r[3] == "CM" ? 0 : 1).ThenBy(r => r[1], StringComparer.Ordinal).ToList();
        foreach (var row in ordered)
        {
            var days = DateOnly.Parse(row[7], CultureInfo.InvariantCulture).DayNumber - new DateOnly(2026, 6, 22).DayNumber;
            var number = 88000 + (int)Math.Round(days * 2.8);
            while (!used.Add(number)) number++;
            row[0] = $"WO-{number}";
        }

        // Other documents cite these numbers (the P-201 asset record, Chapter 17's questions); fail if they drift.
        Expect(ordered, "PN-4471-B -> PN-4471-C", "WO-88412");
        Expect(ordered, "chg mech seal to PN-4471-C", "WO-88454");
        return ordered;
    }

    private static void Expect(List<string[]> rows, string description, string number)
    {
        var row = rows.Single(r => r[5].Contains(description, StringComparison.Ordinal));
        if (row[0] != number) throw new InvalidOperationException($"Expected {number} for \"{description}\", got {row[0]}.");
    }
}
