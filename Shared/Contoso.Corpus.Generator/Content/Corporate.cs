using Contoso.Corpus.Generator.Writers;
using Contoso.Knowledge.Documents;
using static Contoso.Corpus.Generator.Content.Meta;

namespace Contoso.Corpus.Generator.Content;

/// <summary>The corporate library: safety bulletins on the intranet, HR, procurement and IT documents on SharePoint.</summary>
internal static class Corporate
{
    private static readonly string[] Employees = ["employees"];

    public static IEnumerable<SourceSpec> All()
    {
        foreach (var bulletin in Bulletins()) yield return bulletin;
        foreach (var hr in HumanResources()) yield return hr;
        yield return SupplyAgreement();
        yield return PurchasingPolicy();
        yield return AiToolsPolicy();
    }

    private static readonly (string Plant, string Contact, string Extension)[] Plants =
    [
        ("Riverside", "the Riverside maintenance planner", "4410"),
        ("Northgate", "the Northgate maintenance planner", "5417"),
        ("Lakeside", "the Lakeside shift supervisor", "6102"),
    ];

    private static IEnumerable<SourceSpec> Bulletins()
    {
        // SB-0932 exists once per plant: the same bulletin with a plant-specific contact paragraph.
        foreach (var (plant, contact, extension) in Plants)
        {
            var original = plant == "Riverside";
            var id = original ? "SB-0932" : $"SB-0932-{plant.ToUpperInvariant()}";
            var b = new DocBuilder(id, "2", Of(
                original ? "Safety bulletin SB-0932: Seal housing work" : $"Safety bulletin SB-0932: Seal housing work ({plant} plant)",
                "corporate", DocumentTypes.SafetyBulletin, SensitivityLabels.Internal, Employees, "Environment, health and safety",
                revision: "2", from: D(2026, 3, 2), tags: ["CP-200"]));
            b.Seed("sb-0932")
             .P("This bulletin applies to every pump with a mechanical seal flushed by seal water, not only to the CP-200. Supervisors brief their teams on it before the next seal job and record the briefing.")
             .P($"At the {plant} plant, report any seal-water line that can't be isolated or drained to {contact} on extension {extension} before work starts.");
            var path = original ? "intranet/safety/sb-0932.html" : $"intranet/safety/sb-0932-{plant.ToLowerInvariant()}.html";
            yield return new SourceSpec(b.Build("intranet", path), Format.Html, new DateTimeOffset(2026, 3, 2, 7, 0, 0, TimeSpan.Zero))
            {
                Html = Page("SB-0932", "2026-03-02", plant),
                Note = original ? "The original bulletin." : "A regional variant: a near-duplicate of SB-0932 that differs in one paragraph.",
            };
        }

        var hotWork = new DocBuilder("SB-0947", "1", Of("Safety bulletin SB-0947: Hot work permits", "corporate", DocumentTypes.SafetyBulletin,
            SensitivityLabels.Internal, Employees, "Environment, health and safety", revision: "1", from: D(2026, 6, 15)));
        hotWork.Seed("sb-0947")
            .P("The permit is valid for one shift. If the work isn't finished by the end of the shift, the next shift supervisor issues a new permit after checking the site again.");
        yield return new SourceSpec(hotWork.Build("intranet", "intranet/safety/sb-0947.html"), Format.Html, new DateTimeOffset(2026, 6, 15, 7, 0, 0, TimeSpan.Zero))
        {
            Html = Page("SB-0947", "2026-06-15", null),
        };

        (string Id, string Title, DateOnly Issued, string[] Paragraphs)[] others =
        [
            ("SB-0901", "Confined space entry", D(2025, 1, 13),
            [
                "Pump pits, tanks and valve chambers are confined spaces. Enter one only with a confined space permit, a gas test that shows at least 19.5 percent oxygen and no flammable or toxic gas, and an attendant who stays outside.",
                "The attendant keeps a log of everyone inside and calls the emergency team if anyone inside stops responding. The attendant never goes in to rescue.",
            ]),
            ("SB-0915", "Working at height in pump galleries", D(2025, 7, 7),
            [
                "Use a mobile elevating work platform or a scaffold with guardrails for work more than 2 meters above the floor of a pump gallery. Ladders are for access only, not for work.",
                "Close the gallery below to traffic while anyone works overhead, and tie off tools so that they can't fall.",
            ]),
            ("SB-0950", "Chemical cleaning of strainers", D(2026, 8, 3),
            [
                "Some sites descale seal-water strainer screens with citric acid solution. Wear chemical goggles and nitrile gloves, and mix the solution in the workshop, never at the pump.",
                "Rinse the screen with clean water before refitting it; acid left on the screen attacks the mechanical seal's elastomers. WI-FS-031 describes the strainer cleaning itself.",
            ]),
        ];

        foreach (var (id, title, issued, paragraphs) in others)
        {
            var b = new DocBuilder(id, "1", Of($"Safety bulletin {id}: {title}", "corporate", DocumentTypes.SafetyBulletin,
                SensitivityLabels.Internal, Employees, "Environment, health and safety", revision: "1", from: issued));
            b.H(1, $"Safety bulletin {id}: {title}");
            foreach (var p in paragraphs) b.P(p);
            yield return new SourceSpec(b.Build("intranet", $"intranet/safety/{id.ToLowerInvariant()}.html"), Format.Html, new DateTimeOffset(issued.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero))
            {
                Html = Page(id, $"{issued:yyyy-MM-dd}", null),
            };
        }
    }

    private static HtmlPage Page(string id, string issued, string? plant) =>
        new("Safety", $"Home › Safety › Bulletins › {id}{(plant is null || plant == "Riverside" ? "" : $" ({plant})")}",
            [("/safety/sb-0932.html", "SB-0932: Seal housing work"), ("/safety/sb-0947.html", "SB-0947: Hot work permits"), ("/safety/sb-0901.html", "SB-0901: Confined space entry")])
        {
            MetaTags = new Dictionary<string, string> { ["contoso:doc-id"] = id, ["contoso:issued"] = issued, ["contoso:owner"] = "Environment, health and safety" },
        };

    private static IEnumerable<SourceSpec> HumanResources()
    {
        string[] restricted = ["hr", "people-managers"];
        var y2027 = new DocBuilder("HR-PL-2027", "1", Of("HR guidance: parental leave changes for 2027", "corporate", DocumentTypes.Policy,
            SensitivityLabels.Restricted, restricted, "Daniel Mensah", revision: "1", from: D(2027, 1, 18)));
        y2027.Seed("hr-pl-2027")
            .H(2, "Questions managers may hear")
            .P("Employees may ask whether they can split leave they have already started. They can't: the three-block rule applies only to leave that starts on or after March 1, 2027.")
            .P("Employees on fixed-term contracts receive the same leave if their contract runs for at least six months beyond the start of the leave.");
        yield return new SourceSpec(y2027.Build("sharepoint", "sharepoint/hr/HR-PL-2027 Parental leave changes.docx"), Format.Docx, new DateTimeOffset(2027, 1, 18, 16, 40, 0, TimeSpan.Zero))
        {
            Author = "Daniel Mensah",
            Docx = new DocxOptions { Header = "RESTRICTED: HR and people managers only", Properties = new Dictionary<string, string> { ["Sensitivity"] = "Restricted", ["Audience"] = "HR, people managers" } },
            Note = "Restricted until the announcement on February 15, 2027.",
        };

        var y2024 = new DocBuilder("HR-PL-2024", "1", Of("Parental leave policy (2024)", "corporate", DocumentTypes.Policy,
            SensitivityLabels.Restricted, restricted, "Human resources", revision: "1", from: D(2024, 1, 1)));
        y2024.Seed("hr-pl-2024")
            .P("Employees on parental leave keep their pension contributions and accrue annual leave as if they were working.");
        yield return new SourceSpec(y2024.Build("sharepoint", "sharepoint/hr/HR-PL-2024 Parental leave policy.pdf"), Format.Pdf, new DateTimeOffset(2023, 12, 4, 10, 0, 0, TimeSpan.Zero))
        {
            Author = "Human resources",
            Page = PageStyle.Default with { RunningFooter = "HR-PL-2024 · Page {page} of {pages}" },
        };
    }

    private static SourceSpec SupplyAgreement()
    {
        var b = new DocBuilder("SC-2024-118", "1", Of("Supplier contract SC-2024-118: Fabrikam Seals", "corporate", DocumentTypes.Contract,
            SensitivityLabels.Confidential, ["procurement"], "Grace Liu", revision: "1", from: D(2024, 7, 1), to: D(2027, 6, 30),
            parts: ["PN-4471-B", "PN-4471-C"]));
        b.Seed("sc-2024-118")
         .H(2, "1 Parties")
         .P("This supply agreement is made between Contoso Industrial (the buyer) and Fabrikam Seals (the supplier). Each party signs through an authorized representative.")
         .H(2, "2 Products and prices")
         .Table("Schedule A: products and prices", ["Part number", "Description", "Unit price", "Minimum order"],
            ["PN-4471-B", "Cartridge mechanical seal for CP-200 (until September 13, 2026)", "1,180 USD", "4"],
            ["PN-4471-C", "Cartridge mechanical seal for CP-200, silicon-carbide face", "1,340 USD", "4"])
         .P("Prices are fixed for the first 12 months and may then rise by no more than the producer price index for industrial seals, once a year, with 60 days' notice.")
         .H(2, "3 Delivery")
         .P("The supplier delivers to the Contoso plant named in each order, carriage paid. Late deliveries earn the buyer a credit of 1 percent of the order value per week of delay, up to 10 percent.")
         .H(2, "4 Quality")
         .P("The supplier keeps an ISO 9001 certified quality system and supplies a certificate of conformity with every delivery. The buyer may audit the supplier's plant with ten business days' notice.")
         .H(2, "5 Warranty")
         .P("The supplier warrants each seal against defects in material and workmanship for 12 months from installation or 18 months from delivery, whichever ends first. Seals that fail within the warranty are replaced free of charge, and the supplier pays for the failure analysis.")
         .H(2, "6 Termination")
         .P("Either party may end the agreement for material breach that is not remedied within 30 days of written notice. Orders placed before termination are delivered on the agreed terms.")
         .H(2, "7 Signatures")
         .P("Signed for Contoso Industrial by Grace Liu, procurement lead, and for Fabrikam Seals by its sales director.")
         .Table(null, ["For Contoso Industrial", "For Fabrikam Seals"],
            ["Name: Grace Liu", "Name: Jordan Avery"],
            ["Title: Procurement lead", "Title: Sales director"],
            ["Date: June 12, 2024", "Date: June 14, 2024"]);
        var truth = b.Build("sharepoint", "sharepoint/procurement/SC-2024-118 Fabrikam Seals supply agreement.pdf");
        return new SourceSpec(truth, Format.Pdf, new DateTimeOffset(2024, 6, 17, 9, 0, 0, TimeSpan.Zero))
        {
            Author = "Procurement",
            Page = PageStyle.Default with
            {
                RunningHeader = "SC-2024-118 · Confidential",
                RunningFooter = "Page {page} of {pages}",
                BodySize = 11,
                PageBreaksBefore = new HashSet<int> { Fluid.IndexOf(truth, "7 Signatures") },
            },
            ScannedPages = new HashSet<int> { -1 },
            Scan = ScanStyle.Default with
            {
                SkewDegrees = 0.8,
                Marks = [new PaperMark(-1, "Grace Liu", 95, 250, -3, Rgb.Blue, Stamp: false, Size: 20), new PaperMark(-1, "J. Avery", 335, 252, 2, Rgb.Blue, Stamp: false, Size: 20)],
            },
            Note = "Mixed document: the signed last page was printed, signed and scanned back in, so it has no text layer.",
        };
    }

    private static SourceSpec PurchasingPolicy()
    {
        var b = new DocBuilder("PROC-POL-007", "4", Of("Procurement policy PROC-POL-007: Purchase approvals", "corporate", DocumentTypes.Policy,
            SensitivityLabels.Internal, ["procurement"], "Grace Liu", revision: "4", from: D(2025, 7, 1)));
        b.Seed("proc-pol-007")
         .Table("Approval limits", ["Order value", "Approvals needed"],
            ["Up to 25,000 USD", "One approver other than the requester"],
            ["25,000 to 250,000 USD", "The approver and the plant controller"],
            ["Above 250,000 USD", "The approver, the plant controller and the CFO"])
         .P("Splitting an order to stay under an approval limit is a breach of this policy. Emergency orders outside working hours may be placed by the duty manager and approved on the next business day.");
        return new SourceSpec(b.Build("sharepoint", "sharepoint/procurement/PROC-POL-007 Purchase approvals.docx"), Format.Docx, new DateTimeOffset(2025, 6, 20, 12, 0, 0, TimeSpan.Zero))
        {
            Author = "Grace Liu",
            Docx = new DocxOptions { Properties = new Dictionary<string, string> { ["EffectiveDate"] = "2025-07-01" } },
        };
    }

    private static SourceSpec AiToolsPolicy()
    {
        var b = new DocBuilder("IT-POL-003", "2", Of("IT policy IT-POL-003: Using AI tools", "corporate", DocumentTypes.Policy,
            SensitivityLabels.Internal, Employees, "IT security", revision: "2", from: D(2026, 10, 1)));
        b.Seed("it-pol-003")
         .P("Report any document you pasted into a public tool by mistake to the IT service desk, so that the security team can assess it.");
        return new SourceSpec(b.Build("sharepoint", "sharepoint/it/IT-POL-003 Using AI tools.docx"), Format.Docx, new DateTimeOffset(2026, 9, 25, 15, 0, 0, TimeSpan.Zero))
        {
            Author = "IT security",
        };
    }
}
