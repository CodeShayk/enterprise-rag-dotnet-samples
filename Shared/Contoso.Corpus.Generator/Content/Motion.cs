using Contoso.Corpus.Generator.Writers;
using Contoso.Knowledge.Documents;
using static Contoso.Corpus.Generator.Content.Meta;

namespace Contoso.Corpus.Generator.Content;

/// <summary>Motion Systems: the HG-400 gearbox, the VD-75 drive and the RX coupling.</summary>
internal static class Motion
{
    private static readonly string[] MotionAndService = ["motion", "service"];

    public static IEnumerable<SourceSpec> All()
    {
        yield return Hg400Manual();
        yield return Vd75Manual();
        yield return CouplingInstructions();
        yield return OilChangeSop();
        yield return Ecn2305();
    }

    private static SourceSpec Hg400Manual()
    {
        var b = new DocBuilder("HG400-MAN-2021", "2", Of(
            "HG-400 Helical Gearbox Operating Manual", "motion", DocumentTypes.Manual, SensitivityLabels.Internal,
            MotionAndService, "Motion Systems engineering", revision: "2", from: D(2021, 2, 1),
            tags: ["HG-400", "GBX-07"], parts: ["PN-6120-A", "PN-6210-A", "PN-6211-A"]));
        b.H(1, "HG-400 Helical Gearbox Operating Manual")
         .P("Revision 2, February 2021. This manual covers the installation, lubrication, operation and maintenance of HG-400 helical gearboxes with two or three stages.")
         .H(1, "1 Safety")
         .Bullets(
            "Stop and lock out the driving motor before any work on the gearbox.",
            "Let the gearbox cool before opening a plug: the oil can be hotter than 80 °C.",
            "Fit guards over all rotating shafts and couplings before starting.",
            "Lift the gearbox only by its eyebolts, and never with oil in it.")
         .H(1, "2 Product description")
         .P("The HG-400 is a foot-mounted helical gearbox with parallel input and output shafts. Hardened and ground helical gears run in an oil bath, and tapered roller bearings carry the shafts. The housing is cast iron with cooling fins on both sides.")
         .H(2, "2.1 Type designation")
         .P("The type code on the nameplate gives the frame size, the number of stages and the nominal ratio. For example, HG-400-2-12.5 is a two-stage gearbox with a ratio of 12.5 to 1.")
         .Table("Table 2-1 Ratings at 1,500 rpm input", ["Ratio", "Stages", "Output torque", "Thermal rating"],
            ["5.6", "2", "4,200 N·m", "55 kW"],
            ["8", "2", "4,400 N·m", "48 kW"],
            ["12.5", "2", "4,600 N·m", "40 kW"],
            ["16", "2", "4,600 N·m", "35 kW"],
            ["25", "3", "4,800 N·m", "28 kW"],
            ["40", "3", "5,000 N·m", "22 kW"])
         .P("The thermal rating is the power the gearbox can transmit continuously without the oil exceeding 90 °C at an ambient temperature of 25 °C. Above 35 °C ambient, derate it by 10 percent for every 5 °C.")
         .Figure("Figure 2-1 HG-400 exploded view", "Exploded view of the HG-400 showing the housing, shafts, gears and plugs.",
            "1 Housing", "2 Input shaft", "3 Pinion", "4 Output shaft", "5 Breather plug", "6 Sight glass", "7 Drain plug")
         .H(1, "3 Installation")
         .H(2, "3.1 Transport and storage")
         .P("Store the gearbox indoors in a dry place. For storage longer than six months, fill it completely with oil and turn the input shaft by hand every month, so that the bearings and gear teeth stay coated.")
         .Seed("hg400-man-3.2", headingShift: 1)
         .H(2, "3.3 Shaft connections")
         .P("Fit couplings and sprockets by heating them to about 100 °C; never hammer them onto the shafts, because the impact damages the bearings. Keep the overhung load on the output shaft within the value on the nameplate.")
         .H(2, "3.4 Breather")
         .P("The gearbox is shipped with a closed transport plug in the breather position. Replace it with the breather plug supplied before filling the gearbox with oil. A blocked breather lets pressure build up in the housing and pushes oil past the shaft seals.")
         .H(1, "4 Lubrication")
         .Seed("hg400-man-4.1", headingShift: 1)
         .H(2, "4.2 Approved oils")
         .Table("Table 4-1 Approved oils", ["Oil type", "Viscosity grade", "Change interval"],
            ["Mineral gear oil", "ISO VG 220", "5,000 h or 12 months"],
            ["Synthetic PAO gear oil", "ISO VG 220", "10,000 h or 24 months"],
            ["Food-grade gear oil (NSF H1)", "ISO VG 220", "5,000 h or 12 months"])
         .P("Don't mix mineral and synthetic oils. When changing from one type to the other, flush the gearbox with the new oil first.")
         .H(2, "4.3 Oil change")
         .Steps(
            "Run the gearbox until the oil is warm, so that it drains completely, then stop and lock out the motor.",
            "Remove the breather plug and the drain plug and let the oil drain into a container.",
            "Clean the magnetic drain plug and note any metal particles on it in the work order.",
            "Refit the drain plug with a new sealing ring and tighten it to 80 N·m.",
            "Fill the gearbox through the breather opening until the oil reaches the middle of the sight glass.",
            "Refit the breather plug and run the gearbox for ten minutes, then check the level again.")
         .H(1, "5 Operation")
         .H(2, "5.1 Running-in")
         .P("Run a new gearbox at no more than half load for its first 50 hours. The gears' contact pattern settles during running-in, which is why the first oil change is due after only 500 hours.")
         .H(2, "5.2 Operating limits")
         .P("Stop the gearbox and find the cause if the oil temperature exceeds 90 °C, if the noise changes suddenly or if oil leaks from a shaft seal. A whining noise that rises with speed usually points to a low oil level or worn gear teeth.")
         .H(1, "6 Maintenance")
         .Table("Table 6-1 Maintenance schedule", ["Task", "Interval"],
            ["Check the oil level at the sight glass", "Weekly"],
            ["Check for leaks and unusual noise", "Weekly"],
            ["Clean the cooling fins", "Every 3 months"],
            ["Oil analysis for viscosity, water and metal particles", "Every 6 months"],
            ["Change the oil", "As Table 4-1"],
            ["Replace the shaft seals", "Every 20,000 h or when leaking"])
         .H(2, "6.1 Replacing a shaft seal")
         .Steps(
            "Drain the oil below the level of the seal and remove the coupling from the shaft.",
            "Pull out the old seal without scratching the shaft or the housing bore.",
            "Polish out small scratches on the shaft with fine emery cloth; replace the shaft sleeve if the seal lip has worn a groove.",
            "Press in the new seal, lip facing the oil, with a sleeve that bears on its outer ring.")
         .H(1, "7 Troubleshooting")
         .Table("Table 7-1 Troubleshooting", ["Symptom", "Possible cause", "Remedy"],
            ["Whining noise that rises with speed", "Low oil level or worn gear teeth", "Top up the oil; inspect the gears if the noise stays"],
            ["Knocking noise once per revolution", "Damaged gear tooth", "Stop the gearbox and inspect the gears"],
            ["Oil leaks at a shaft", "Worn shaft seal or blocked breather", "Clean the breather; replace the seal"],
            ["Oil temperature above 90 °C", "Overload, wrong oil or dirty cooling fins", "Check the load and the oil; clean the fins"],
            ["Foam in the sight glass", "Water in the oil or mixed oils", "Change the oil and find the source of water"]);

        return new SourceSpec(b.Build("fileshare", "fileshare/motion/manuals/HG-400 Gearbox Manual 2021.pdf"), Format.Pdf, new DateTimeOffset(2021, 2, 1, 10, 0, 0, TimeSpan.Zero))
        {
            Author = "Motion Systems engineering",
            Page = PageStyle.Default with
            {
                Columns = 2,
                BodySize = 9.5,
                Hyphenate = true,
                RowMajorContent = true,
                RunningHeader = "HG-400 Helical Gearbox · Operating Manual · Revision 2",
                RunningFooter = "Contoso Industrial, Motion Systems · {page}",
            },
            Note = "Two columns written line by line across the page, so content order isn't reading order; hyphenated line ends; a vector figure with labels.",
        };
    }

    private static readonly (string Code, string Name, string Cause, string Remedy)[] Faults =
    [
        ("F001", "DC bus undervoltage", "Supply voltage dip or a missing phase", "Check the supply; the drive restarts when the voltage returns"),
        ("F002", "DC bus overvoltage", "Ramp-down time too short for the load's inertia", "Raise parameter 2-11 or fit a brake resistor"),
        ("F003", "Heatsink overtemperature", "Blocked air flow or a failed fan", "Clean the air path; check the fan (F016)"),
        ("F004", "Earth fault", "Damaged motor cable or wet motor", "Measure the insulation before resetting"),
        ("F005", "Motor overload (I²t)", "Motor current above its rating for too long", "Check the load; check parameter 1-03"),
        ("F006", "Input phase loss", "A blown supply fuse", "Replace the fuse and find out why it blew"),
        ("F007", "Motor phase loss", "Loose motor cable terminal", "Tighten the motor terminals"),
        ("F008", "Fieldbus timeout", "No message from the controller for 2 seconds", "Check the network cable and the controller"),
        ("F009", "Brake resistor overload", "Too many fast stops", "Lengthen the ramp-down time"),
        ("F010", "Analog input lost", "Speed reference below 2 mA on a 4 to 20 mA input", "Check the reference cable and its source"),
        ("F011", "Output short circuit", "Short circuit in the motor cable or motor", "Disconnect and test the motor cable"),
        ("F013", "Motor stall", "Motor below 5 percent of set speed at full current", "Free the jammed load"),
        ("F015", "Parameter memory error", "Corrupt parameters after a power cut during a save", "Restore the parameter backup"),
        ("F016", "Cooling fan failure", "Fan blocked or worn out", "Replace fan PN-7716-A"),
        ("F017", "External fault", "The external fault input opened", "Check the device wired to the input"),
        ("F018", "Safe torque off active", "The safety circuit removed the enable signal", "Close the safety circuit, then reset"),
        ("F020", "Overspeed", "Motor speed above 120 percent of maximum frequency", "Check the load for overhauling forces"),
        ("F021", "Underload", "Motor current below 15 percent while running", "Check for a broken belt or coupling"),
    ];

    private static SourceSpec Vd75Manual()
    {
        var b = new DocBuilder("VD75-MAN-2022", "1", Of(
            "VD-75 Variable Frequency Drive: Installation and Fault Guide", "motion", DocumentTypes.Manual, SensitivityLabels.Internal,
            MotionAndService, "Motion Systems engineering", revision: "1", from: D(2022, 6, 15), tags: ["VD-75"], parts: ["PN-7716-A"]));
        b.H(1, "VD-75 Variable Frequency Drive: Installation and Fault Guide")
         .H(1, "1 Overview")
         .P("The VD-75 drives three-phase induction motors from 0.75 to 75 kW. It controls speed through the output frequency and protects the motor and itself through the faults in section 4. Contoso fits VD-75 drives to conveyors, mixers and pumps that need variable speed.")
         .H(1, "2 Installation")
         .Bullets(
            "Mount the drive vertically with 100 mm of free space above and below it for cooling air.",
            "Use shielded motor cable and earth the shield at both ends with 360-degree clamps.",
            "Keep motor cables at least 300 mm from signal cables, and cross them at right angles.",
            "Don't fit contactors between the drive and the motor; switching under load damages the output stage.")
         .H(1, "3 Key parameters")
         .Table("Table 3-1 Key parameters", ["Parameter", "Name", "Default", "Range"],
            ["1-03", "Motor rated current", "Drive rating", "0.2 to 1.5 times the drive rating"],
            ["1-04", "Motor rated speed", "1,450 rpm", "100 to 6,000 rpm"],
            ["2-10", "Ramp-up time", "2 s", "0.1 to 600 s"],
            ["2-11", "Ramp-down time", "2 s", "0.1 to 600 s"],
            ["3-01", "Maximum frequency", "50 Hz", "5 to 200 Hz"],
            ["4-05", "Automatic restart attempts", "3", "0 to 10"])
         .H(1, "4 Faults")
         .P("A fault stops the motor and shows its code on the display. Table 4-1 lists the faults; the most common ones have their own sections after the table. Reset a fault with the STOP/RESET key once its cause is removed.")
         .Table("Table 4-1 Fault codes", ["Code", "Fault", "Likely cause", "Remedy"],
            [.. Faults.Select(f => new[] { f.Code, f.Name, f.Cause, f.Remedy })])
         .Seed("vfd-f012", headingShift: 1)
         .H(2, "Fault F002: DC bus overvoltage")
         .P("Fault F002 means the voltage of the drive's internal DC bus exceeded 800 V. It happens when the motor feeds energy back into the drive faster than the drive can absorb it, usually during a fast stop of a heavy load. Raise parameter 2-11 (ramp-down time), or fit a brake resistor if the process needs fast stops.")
         .H(2, "Fault F010: analog input lost")
         .P("Fault F010 means the speed reference on a 4 to 20 mA input fell below 2 mA, which the drive reads as a broken wire. The drive stops the motor rather than run at an unknown speed. Check the reference cable and the device that sends the signal.");
        return new SourceSpec(b.Build("fileshare", "fileshare/motion/manuals/VD-75 Drive Manual 2022.pdf"), Format.Pdf, new DateTimeOffset(2022, 6, 15, 12, 0, 0, TimeSpan.Zero))
        {
            Author = "Motion Systems engineering",
            Page = PageStyle.Default with { RunningHeader = "VD-75 Installation and Fault Guide", RunningFooter = "Page {page}", WordFragments = true, RuledTables = false },
            Note = "Every word written as its own text object, and a fault table without ruling lines.",
        };
    }

    private static SourceSpec CouplingInstructions()
    {
        var b = new DocBuilder("CPL-RX-INST", "1", Of(
            "RX flexible coupling: installation and alignment", "motion", DocumentTypes.Manual, SensitivityLabels.Internal,
            MotionAndService, "Motion Systems engineering", revision: "1", from: D(2020, 9, 1), parts: ["PN-5120-A", "PN-5190-A"]));
        b.Seed("cpl-rx-inst")
         .Table("RX coupling sizes", ["Size", "Maximum bore", "Maximum torque", "Elastomer element"],
            ["50", "50 mm", "450 N·m", "PN-5120-A"],
            ["70", "70 mm", "1,250 N·m", "PN-5170-A"],
            ["90", "90 mm", "2,800 N·m", "PN-5190-A"],
            ["110", "110 mm", "5,200 N·m", "PN-5210-A"])
         .P("For couplings above size 90, halve the permissible misalignment in the table above.");
        return new SourceSpec(b.Build("fileshare", "fileshare/motion/manuals/RX Coupling Installation.pdf"), Format.Pdf, new DateTimeOffset(2020, 9, 1, 9, 0, 0, TimeSpan.Zero))
        {
            Author = "Motion Systems engineering",
            Page = PageStyle.Default with { RunningFooter = "RX coupling installation instructions · Page {page} of {pages}" },
        };
    }

    private static SourceSpec OilChangeSop()
    {
        var b = new DocBuilder("SOP-MS-003", "2", Of(
            "SOP-MS-003: Oil change on HG-400 gearboxes", "motion", DocumentTypes.Sop, SensitivityLabels.Internal,
            MotionAndService, "Motion Systems maintenance", revision: "2", from: D(2025, 1, 20), tags: ["HG-400", "GBX-07"]));
        b.H(1, "SOP-MS-003: Oil change on HG-400 gearboxes")
         .P("Revision 2, effective January 20, 2025. This procedure applies the oil change in section 4.3 of the HG-400 manual at Contoso plants and adds the checks that the plants' oil analysis program needs.")
         .Steps(
            "Lock out the driving motor as SOP-FS-007 describes for pumps; the same steps apply to gearboxes.",
            "Take an oil sample from the middle of the sight glass level before draining, and label it with the gearbox tag.",
            "Drain the warm oil, then clean and inspect the magnetic drain plug.",
            "Refill with ISO VG 220 oil of the type on the gearbox's lubrication label: 18 liters for an HG-400.",
            "Run the gearbox for ten minutes and check the level and the plugs for leaks.",
            "Record the oil type, the quantity and the operating hours in the work order, and send the sample to the laboratory.")
         .P("Gearbox GBX-07 on the Lakeside packaging line runs on synthetic PAO oil, so its change interval is 10,000 hours.");
        return new SourceSpec(b.Build("fileshare", "fileshare/motion/sops/SOP-MS-003 Gearbox oil change.docx"), Format.Docx, new DateTimeOffset(2025, 1, 14, 9, 0, 0, TimeSpan.Zero))
        {
            Author = "Motion Systems maintenance",
            Docx = new DocxOptions { Header = "SOP-MS-003 · Revision 2", Properties = new Dictionary<string, string> { ["EffectiveDate"] = "2025-01-20" } },
        };
    }

    private static SourceSpec Ecn2305()
    {
        var b = new DocBuilder("ECN-2305", "1", Of(
            "Engineering change notice ECN-2305: HG-400 breather plug", "motion", DocumentTypes.ChangeNotice, SensitivityLabels.Internal,
            ["motion"], "Motion Systems engineering", from: D(2026, 12, 1), supersedes: ["PN-6120-A"], tags: ["HG-400"], parts: ["PN-6120-A", "PN-6120-B"]));
        b.H(1, "Engineering change notice ECN-2305")
         .Table(null, ["Field", "Entry"],
            ["Change notice", "ECN-2305"],
            ["Title", "HG-400 breather plug"],
            ["Product", "HG-400 helical gearbox"],
            ["Originator", "Motion Systems engineering"],
            ["Date raised", "October 30, 2026"],
            ["Effective", "December 1, 2026"])
         .P("Effective December 1, 2026, breather plug PN-6120-A is superseded by PN-6120-B, which has a desiccant filter. Dust and moisture drawn in through the open breather were found in the oil of gearboxes at the Lakeside and Eastfield plants.")
         .P("Fit PN-6120-B at the next oil change. Replace the desiccant cartridge when its color changes from blue to pink.")
         .Table("Affected parts", ["Old part", "New part", "Disposition"],
            ["PN-6120-A", "PN-6120-B", "Replace at the next oil change"]);
        return new SourceSpec(b.Build("fileshare", "fileshare/motion/changes/ECN-2305.pdf"), Format.Pdf, new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero))
        {
            Author = "Document control",
            Page = PageStyle.Default with { RunningHeader = "Contoso Industrial · Engineering change notice", RunningFooter = "Form ENG-07 rev 4 · Page {page} of {pages}" },
            ScannedAll = true,
            Scan = ScanStyle.Default with { SkewDegrees = 2.5, Dpi = 200, JpegQuality = 55, Marks = [new PaperMark(1, "APPROVED\n2026-10-31", 400, 92, -5, Rgb.Red)] },
            Note = "A poorer scan: 200 DPI, stronger skew and heavier JPEG compression.",
        };
    }
}
