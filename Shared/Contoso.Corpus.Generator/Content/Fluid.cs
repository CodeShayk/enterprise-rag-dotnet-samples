using Contoso.Corpus.Generator.Writers;
using Contoso.Knowledge.Documents;
using static Contoso.Corpus.Generator.Content.Meta;

namespace Contoso.Corpus.Generator.Content;

/// <summary>Fluid Systems: the CP-200 pump's manuals, procedures, change notices, training and asset records.</summary>
internal static class Fluid
{
    private static readonly string[] FluidAndService = ["fluid", "service"];

    public static IEnumerable<SourceSpec> All()
    {
        yield return Cp200Manual();
        var guide = TroubleshootingGuide();
        yield return guide;
        yield return guide with
        {
            // The service library keeps its own copy of the guide: the same bytes under another name and owner.
            Truth = guide.Truth with
            {
                Id = "SVC-LIB-0042",
                Version = "1",
                Metadata = guide.Truth.Metadata with { Title = "CP-200 troubleshooting (service library copy)", Owner = "Field Services library" },
                Source = guide.Truth.Source with { Uri = "fileshare/service/library/CP-200 troubleshooting.pdf" },
            },
            CopyOf = guide.Path,
            Note = "Byte-for-byte copy of the CP-200 troubleshooting guide in the service library.",
        };
        foreach (var sop in SealSop()) yield return sop;
        yield return IsolationSop();
        yield return StrainerInstruction();
        yield return Ecn2291();
        yield return Ecn2240();
        yield return TrainingDeck();
        foreach (var page in AssetPages()) yield return page;
    }

    private static SourceSpec Cp200Manual()
    {
        var b = new DocBuilder("CP200-MAN-2009", "2009", Of(
            "CP-200 Centrifugal Pump Operating and Maintenance Manual", "fluid", DocumentTypes.Manual, SensitivityLabels.Internal,
            FluidAndService, "Fluid Systems engineering", revision: "2009-1", from: D(2009, 6, 1),
            tags: ["CP-200"], parts: ["PN-4471-B", "PN-3310-A", "PN-3311-A", "PN-2205-C", "PN-2840-A", "PN-5120-A"]));

        b.H(1, "CP-200 Centrifugal Pump Operating and Maintenance Manual")
         .P("Edition 2009. Contoso Industrial, Fluid Systems division. Keep this manual with the pump for the life of the equipment and pass it on with the pump if it is moved or sold.")
         .H(1, "1 Safety")
         .P("Read this manual before installing, operating or servicing the pump. Work on the pump only when you are trained for the task and the pump is isolated as your site's procedures require.")
         .Bullets(
            "Isolate the motor electrically and lock it out before any work on the pump.",
            "Release the pressure in the casing and the seal-water line before opening any connection.",
            "Wear safety glasses and gloves when handling the mechanical seal. The seal faces are brittle and their edges are sharp.",
            "Never run the pump dry or against a closed suction valve.",
            "Lift the pump only by the lifting lugs on the baseplate, never by the motor eyebolt.")
         .H(1, "2 Description")
         .P("The CP-200 is a single-stage, end-suction centrifugal pump with a closed impeller, designed for clean or slightly contaminated water at up to 90 °C. The pump and its motor sit on a common baseplate and are joined by a flexible spacer coupling, so the rotating assembly can be removed without disturbing the pipework.")
         .P("The shaft is sealed by a cartridge mechanical seal flushed with clean seal water from an external supply. Two grease-lubricated ball bearings in the bearing housing carry the shaft. A controller on the pump skid monitors the motor, the bearing temperatures and the seal-water pressure and trips the pump when a limit is exceeded.")
         .H(1, "3 Technical data")
         .Table("Table 3-1 Technical data", ["Item", "Value"],
            ["Flow range", "40 to 240 m³/h"],
            ["Head range", "15 to 55 m"],
            ["Maximum casing pressure", "16 bar"],
            ["Maximum liquid temperature", "90 °C"],
            ["Speed", "1,480 rpm at 50 Hz"],
            ["Motor power", "15 to 45 kW"],
            ["Seal-water pressure", "2.0 to 3.0 bar"],
            ["Weight of the pump without motor", "310 kg"])
         .H(1, "4 Installation")
         .H(2, "4.1 Foundation and baseplate")
         .P("Set the baseplate on a concrete foundation at least 1.5 times the weight of the pump and motor. Level the baseplate to within 0.2 mm per meter with shims next to the anchor bolts, then grout it. Check the coupling alignment after the grout has cured, because tightening the anchor bolts distorts an ungrouted baseplate.")
         .H(2, "4.2 Pipework")
         .P("Support the suction and discharge pipes independently of the pump; the pump flanges must carry no pipe loads. Make the suction pipe at least one size larger than the suction flange and keep it free of high points where air can collect. Fit a strainer in the suction line during the first weeks of operation and remove it once the system is clean.")
         .H(2, "4.3 Seal-water connection")
         .P("Connect the seal-water supply to the flush connection marked SW on the seal housing. Fit an isolation valve, a strainer and a pressure gauge in the line. The seal-water pressure must exceed the suction pressure by at least 1 bar whenever the pump runs.")
         .H(1, "5 Commissioning")
         .H(2, "5.1 Checks before the first start")
         .Steps(
            "Check that the rotating assembly turns freely by hand.",
            "Check the coupling alignment and fit the coupling guard.",
            "Check that the bearings are greased; pumps leave the factory greased for 4,000 hours.",
            "Flush the suction pipe and the seal-water line before connecting them.")
         .Seed("cp200-man-2009-5.4", headingShift: 1)
         .H(2, "5.5 Shutdown")
         .Steps(
            "Close the discharge valve slowly.",
            "Stop the motor.",
            "Close the seal-water supply only after the pump has stopped, and only if the pump will stay stopped.",
            "Drain the casing if the pump may be exposed to frost.")
         .H(1, "6 Assembly and disassembly")
         .H(2, "6.1 Removing the rotating assembly")
         .Steps(
            "Isolate and drain the pump, then remove the coupling guard and the coupling spacer.",
            "Undo the casing bolts and pull the rotating assembly back with the bearing housing.",
            "Mark the position of the impeller nut before loosening it.")
         .H(2, "6.2 Impeller clearance")
         .P("Set the clearance between the impeller and the casing wear ring to 0.3 to 0.5 mm. Replace the wear ring when the clearance exceeds 1.0 mm.")
         .Seed("cp200-man-2009-6.3", headingShift: 1)
         .H(2, "6.4 Seal housing bolts")
         .P("Tighten the four seal housing bolts to 45 N·m. Fit the seal housing before the bearing housing is bolted back to the casing, so that the seal can be centered on the shaft.")
         .H(1, "7 Maintenance")
         .H(2, "7.1 Routine checks")
         .Bullets(
            "Daily: listen for unusual noise and check the seal-water pressure at the gauge.",
            "Weekly: check the seal for leaks; a few drops an hour are normal while the seal runs in.",
            "Monthly: check the bearing temperatures and the motor current.")
         .Seed("cp200-man-2009-7.2", headingShift: 1)
         .H(2, "7.3 Lubrication")
         .P("Regrease each bearing through its grease nipple with the pump running. Use lithium-complex grease of NLGI grade 2, and wipe off the old grease that comes out of the relief plug.")
         .H(1, "8 Spare parts")
         .H(2, "8.1 Mechanical seal and recommended spares")
         .P("The CP-200 uses cartridge mechanical seal PN-4471-B, supplied by Fabrikam Seals. Order the seal complete; Contoso doesn't stock seal faces separately. Keep the spares in Table 8-1 at every site that runs CP-200 pumps.")
         .Table("Table 8-1 Recommended spare parts", ["Part", "Part number", "Quantity"],
            ["Mechanical seal", "PN-4471-B", "1"],
            ["Ball bearing, pump end", "PN-3310-A", "1"],
            ["Ball bearing, drive end", "PN-3311-A", "1"],
            ["Casing gasket", "PN-2205-C", "2"],
            ["Wear ring", "PN-2840-A", "1"],
            ["Coupling element", "PN-5120-A", "1"])
         .Figure("Figure 8-1 Seal housing, exploded view", "Exploded view of the CP-200 seal housing showing the parts in order along the shaft.",
            "1 Seal housing", "2 Gland nuts", "3 Seal PN-4471-B", "4 Shaft sleeve", "5 Connection SW")
         .H(1, "9 Troubleshooting")
         .P("The controller shows an error code for most faults; the CP-200 troubleshooting guide explains each code. Table 9-1 lists faults that the controller can't detect.")
         .Table("Table 9-1 Troubleshooting", ["Symptom", "Possible cause", "Remedy"],
            ["Pump doesn't deliver", "Suction valve closed or casing not vented", "Open the suction valve fully and vent the casing"],
            ["Delivery falls off over weeks", "Worn wear ring or impeller", "Check the impeller clearance (section 6.2)"],
            ["High bearing temperature", "Too much or too little grease", "Regrease as section 7.3 describes"],
            ["Seal leaks steadily", "Worn seal faces", "Replace the mechanical seal (section 8.1)"],
            ["Motor overloads", "Pump runs far right of its curve", "Throttle the discharge valve to the duty point"]);

        var spec = new SourceSpec(b.Build("fileshare", "fileshare/fluid/manuals/CP-200 Manual 2009.pdf"), Format.Pdf, new DateTimeOffset(2011, 5, 20, 9, 0, 0, TimeSpan.Zero))
        {
            Author = "Contoso Industrial, Fluid Systems",
            Page = PageStyle.Default with { RunningHeader = "CP-200 Operating and Maintenance Manual · Contoso Industrial · 2009", RunningFooter = "Page {page} of {pages}", BodySize = 10.5 },
            ScannedAll = true,
            Scan = ScanStyle.Default with
            {
                Marks =
                [
                    new PaperMark(1, "CONTROLLED COPY\nRiverside plant", 360, 120, -6, Rgb.Red),
                    new PaperMark(3, "see ECN-2291: fit PN-4471-C  NR", 300, 650, -4, Rgb.Blue, Stamp: false, Size: 13),
                ],
            },
            Note = "Scanned in 2011 from the printed 2009 edition: no text layer, skew, specks, a stamp and a handwritten note. Table 6-2 holds the M12 flange-bolt torque.",
        };
        return spec;
    }

    private static readonly (string Code, string Title, string Meaning, string Condition, string[] Causes, string Clear, bool Trips)[] ErrorCodes =
    [
        ("E-101", "motor overload", "Error E-101 means the motor current stayed above the overload setting for longer than the trip delay.",
            "The overload relay trips after 10 seconds above 110 percent of the motor's rated current.",
            ["The pump runs far to the right of its curve because the discharge valve is open too far.", "The impeller rubs on the wear ring after a bearing failure.", "The overload relay is set below the motor's rated current."],
            "Find and remove the cause, then reset E-101 at the local panel. The relay resets itself after two minutes.", true),
        ("E-102", "supply phase missing", "Error E-102 means one of the three supply phases is missing.",
            "The controller checks the phases at every start and once a second while the pump runs.",
            ["A blown fuse in the motor feeder.", "A loose terminal in the motor control center."],
            "Have an electrician check the fuses and terminals. E-102 clears itself when all three phases return.", true),
        ("E-103", "wrong phase sequence", "Error E-103 means the supply phase sequence is reversed, so the pump would run backwards.",
            "The controller blocks the start; it never trips a running pump for E-103.",
            ["Two supply cables were swapped during electrical work."],
            "Have an electrician swap two phases at the motor terminal box, then confirm the direction of rotation against the arrow on the bearing housing.", false),
        ("E-105", "earth fault", "Error E-105 means the controller measured a leakage current to earth above 300 mA.",
            "The earth-fault relay trips the pump at once.",
            ["Water in the motor terminal box.", "Damaged motor cable insulation."],
            "Don't reset E-105 until an electrician has measured the motor's insulation resistance.", true),
        ("E-106", "supply undervoltage", "Error E-106 means the supply voltage stayed below 90 percent of its nominal value for more than 5 seconds.",
            "The pump restarts by itself 30 seconds after the voltage returns, unless automatic restart is disabled.",
            ["A weak supply during the start of another large motor.", "A failing transformer tap changer."],
            "E-106 clears itself. If it repeats, record the times and ask the plant electrical engineer to check the supply.", true),
        ("E-201", "suction pressure low", "Error E-201 means the suction pressure fell below 0.3 bar.",
            "The controller warns for 20 seconds before it trips the pump, to ride through short dips.",
            ["The suction valve is partly closed.", "The suction strainer is blocked.", "The level in the suction tank is too low."],
            "Open the suction valve fully, clean the suction strainer and check the tank level before resetting E-201.", true),
        ("E-202", "discharge pressure high", "Error E-202 means the discharge pressure rose above 14 bar.",
            "The controller trips the pump after 5 seconds above the limit.",
            ["The discharge valve is closed.", "A check valve downstream is stuck shut."],
            "Open the discharge path, then reset E-202 at the local panel.", true),
        ("E-203", "dry running", "Error E-203 means the dry-running sensor in the casing detected no liquid.",
            "The controller trips the pump at once, because a few seconds of dry running damage the mechanical seal.",
            ["The casing wasn't vented after maintenance.", "The suction tank ran empty."],
            "Vent the casing until water flows without air, then reset E-203. Check the seal for leaks after the restart.", true),
        ("E-204", "flow low", "Error E-204 means the flow fell below 40 m³/h, the pump's minimum continuous flow.",
            "The controller warns at once and trips the pump after 2 minutes of low flow.",
            ["The discharge valve is throttled too far.", "The process no longer needs the pump."],
            "Open the discharge valve or stop the pump, then reset E-204.", true),
        ("E-211", "seal leakage detected", "Error E-211 means the leakage sensor below the seal housing detected liquid.",
            "E-211 is a warning; the pump keeps running.",
            ["The mechanical seal is worn.", "The seal-water connection leaks."],
            "Plan a seal replacement if the leak continues; SOP-FS-014 describes the work.", false),
        ("E-218", "seal-water pressure high", "Error E-218 means the seal-water pressure rose above 4.0 bar.",
            "E-218 is a warning: high seal-water pressure wastes water and can push water into the process.",
            ["The seal-water pressure regulator failed open."],
            "Set the regulator to 2.5 bar and check that the gauge reads 2.0 to 3.0 bar.", false),
        ("E-221", "bearing temperature high", "Error E-221 means a bearing temperature exceeded 95 °C.",
            "The controller warns at 85 °C and trips the pump at 95 °C.",
            ["Too much grease after regreasing.", "Misalignment between pump and motor.", "A worn bearing."],
            "Let the bearings cool, check the alignment and the amount of grease, then reset E-221.", true),
        ("E-222", "vibration high", "Error E-222 means the vibration on the bearing housing exceeded 7.1 mm/s RMS.",
            "The controller warns at 4.5 mm/s RMS and trips the pump at 7.1 mm/s RMS.",
            ["Misalignment after maintenance.", "Cavitation.", "Worn bearings or a damaged impeller."],
            "Section 3.1 describes how to find the cause. Don't restart the pump repeatedly after E-222.", true),
        ("E-231", "liquid temperature high", "Error E-231 means the liquid temperature in the casing exceeded 90 °C.",
            "The controller trips the pump after 10 seconds above the limit.",
            ["The pump ran against a closed discharge valve.", "The process supplies liquid hotter than the pump's rating."],
            "Let the casing cool and find out why the liquid overheated before resetting E-231.", true),
        ("E-301", "suction pressure sensor fault", "Error E-301 means the signal from suction pressure transmitter PT-11 is outside 4 to 20 mA.",
            "The pump keeps running without low-suction protection, so the controller raises an alarm.",
            ["A broken transmitter cable.", "A failed transmitter."],
            "Check the cable and replace PT-11 if its output stays out of range.", false),
        ("E-302", "seal-water pressure switch fault", "Error E-302 means pressure switch PS-31 reports pressure while the seal-water supply is closed.",
            "The controller checks PS-31 each time the seal-water valve closes.",
            ["A stuck pressure switch.", "A short circuit in the switch cable."],
            "Replace PS-31. Until it is replaced, check the seal-water pressure at the local gauge every shift.", false),
        ("E-303", "bearing temperature sensor fault", "Error E-303 means one of the bearing temperature sensors TE-21 and TE-22 reports an open circuit.",
            "The pump keeps running without bearing temperature protection on that bearing.",
            ["A loose sensor connector.", "A failed sensor."],
            "Reseat the connector; replace the sensor if E-303 returns.", false),
        ("E-305", "controller memory error", "Error E-305 means the controller found corrupt parameters at power-up and loaded the factory defaults.",
            "The controller blocks the start until the parameters are confirmed.",
            ["A power cut while parameters were being saved."],
            "Restore the pump's parameter set from the site's backup and confirm it on the local panel.", false),
        ("E-310", "communication lost", "Error E-310 means the controller hasn't heard from the plant control system for 30 seconds.",
            "The pump keeps running at its last set point.",
            ["A network fault.", "The plant control system was restarted."],
            "E-310 clears itself when communication returns.", false),
        ("E-320", "emergency stop", "Error E-320 means an emergency stop button was pressed.",
            "The pump stops at once and can't restart until the button is released and E-320 is reset.",
            ["Someone pressed an emergency stop button."],
            "Find out why the button was pressed before releasing it and resetting E-320.", true),
        ("E-330", "clock battery low", "Error E-330 means the battery that keeps the controller's clock running is nearly empty.",
            "E-330 is a warning; event logs lose their time stamps once the battery is empty.",
            ["The battery is more than five years old."],
            "Replace the CR2032 battery behind the controller's front panel.", false),
    ];

    private static SourceSpec TroubleshootingGuide()
    {
        var b = new DocBuilder("CP200-TRB-2019", "2019", Of(
            "CP-200 Troubleshooting Guide", "fluid", DocumentTypes.Troubleshooting, SensitivityLabels.Internal,
            FluidAndService, "Fluid Systems service engineering", revision: "3", from: D(2019, 4, 1),
            tags: ["CP-200", "V-114", "PS-31"]));

        b.H(1, "CP-200 Troubleshooting Guide")
         .H(1, "1 Using this guide")
         .P("This guide explains the error codes that the CP-200 controller shows and the mechanical problems it can't detect. Each code's section says what the code means, when the controller raises it, the likely causes in the order to check them and how to clear it. Codes starting with E-1 concern the motor and its supply, E-2 the hydraulics and the seal, and E-3 the controller and its sensors.")
         .P("Work through the causes in order. If you clear a code and it returns within an hour, stop and raise a work order instead of resetting it again.")
         .H(1, "2 Controller error codes")
         .Table("Table 2-1 Error codes at a glance", ["Code", "Meaning", "Trips the pump"],
            [.. ErrorCodes.Select(e => new[] { e.Code, Capitalize(e.Title), e.Trips ? "Yes" : "No" })
                .Append(["E-104", "Motor winding temperature high", "Yes"])
                .Append(["E-217", "Seal-water pressure low", "Yes"])
                .OrderBy(r => r[0], StringComparer.Ordinal)]);

        foreach (var code in ErrorCodes.Select(e => e.Code).Append("E-104").Append("E-217").Order(StringComparer.Ordinal))
        {
            if (code == "E-104")
            {
                b.Seed("cp200-trb-e104", headingShift: 1);
                continue;
            }

            if (code == "E-217")
            {
                b.Seed("cp200-trb-e217", headingShift: 1);
                continue;
            }

            var e = ErrorCodes.Single(x => x.Code == code);
            b.H(2, $"Error {e.Code}: {e.Title}")
             .P(e.Meaning, e.Condition)
             .P("Causes, most likely first:");
            b.Steps(e.Causes);
            b.P(e.Clear);
        }

        b.H(1, "3 Mechanical problems")
         .Seed("cp200-trb-vibration", headingShift: 1)
         .H(2, "Cavitation")
         .P("Cavitation is the collapse of vapor bubbles that form where the pressure at the impeller inlet falls below the liquid's vapor pressure. It sounds like gravel rattling in the casing and erodes the impeller vanes within weeks.")
         .P("Check the suction side first: the suction valve must be fully open, the suction strainer clean and the tank level above its minimum. If the pump runs far to the right of its curve, throttle the discharge valve back to the duty point.")
         .H(2, "Seal leakage")
         .P("A new mechanical seal may drip for the first few hours while its faces run in. A seal that leaks steadily after that has worn or damaged faces. The most common causes are dry running, seal-water pressure below 1.5 bar and abrasive particles in the seal water. Replace the seal as SOP-FS-014 describes; don't try to stop a leak by tightening the gland nuts beyond their torque.")
         .H(2, "Bearing overheating")
         .P("Bearings run hottest in the first hours after regreasing, while excess grease is pushed out. A bearing that stays above 85 °C has too much grease, too little grease, a misaligned coupling or wear. Check the alignment and the amount of grease before replacing the bearing.");

        return new SourceSpec(b.Build("fileshare", "fileshare/fluid/manuals/CP-200 Troubleshooting Guide 2019.pdf"), Format.Pdf, new DateTimeOffset(2019, 4, 1, 8, 30, 0, TimeSpan.Zero))
        {
            Author = "Fluid Systems service engineering",
            Page = PageStyle.Default with { RunningHeader = "CP-200 Troubleshooting Guide · Revision 3 · April 2019", RunningFooter = "Contoso Industrial · Page {page} of {pages}" },
            Note = "Born-digital PDF with a running header and footer on every page and a long table that repeats its header row.",
        };
    }

    private static IEnumerable<SourceSpec> SealSop()
    {
        string[] purpose =
        [
            "This procedure describes how to replace the cartridge mechanical seal on CP-200 pumps safely and without damaging the new seal.",
            "It applies to every CP-200 pump at Contoso plants and to Field Services work on customers' CP-200 pumps.",
        ];
        string[] tools =
        [
            "Mechanical seal cartridge (see step 4) and new casing gaskets.",
            "Torque wrench covering 5 to 60 N·m.",
            "Lockout padlocks and tags for each person working on the pump.",
        ];

        // Revision 3: the seed text, plus purpose, scope, tools and records. Left with its tracked changes visible.
        var rev3 = new DocBuilder("SOP-FS-014", "3", Of(
            "SOP-FS-014: Mechanical seal replacement on CP-200 pumps", "fluid", DocumentTypes.Sop, SensitivityLabels.Internal,
            FluidAndService, "Nadia Rahman", revision: "3", from: D(2026, 9, 14),
            supersedes: ["SOP-FS-014 rev 2"], tags: ["CP-200", "P-201"], parts: ["PN-4471-C"]));
        rev3.Seed("sop-fs-014");
        rev3.H(2, "Purpose and scope").P(purpose).H(2, "Tools and parts").Bullets(tools)
            .H(2, "Records").P("Record the seal's part number, the pump's operating hours and the names of the people who did the work in the work order. Return the old seal to the stores for failure analysis if it failed before 8,000 hours.");
        var rev3Truth = rev3.Build("fileshare", "fileshare/fluid/sops/SOP-FS-014 Mechanical seal replacement Rev 3.docx");
        var step4 = IndexOf(rev3Truth, "Fit mechanical seal PN-4471-C.");
        var step5 = IndexOf(rev3Truth, "Tighten the seal gland nuts evenly to 25 N·m.");
        yield return new SourceSpec(rev3Truth, Format.Docx, new DateTimeOffset(2026, 9, 10, 14, 5, 0, TimeSpan.Zero))
        {
            Author = "Nadia Rahman",
            Docx = new DocxOptions
            {
                Header = "SOP-FS-014 · Revision 3 · Controlled copy",
                Footer = "Uncontrolled when printed. Check the SOP library for the current revision.",
                Changes =
                [
                    new TrackedChange(step4, "PN-4471-B", "PN-4471-C", "Nadia Rahman", new DateTime(2026, 9, 8, 10, 12, 0)),
                    new TrackedChange(step4, "", "Don't mix PN-4471-B and PN-4471-C parts.", "Nadia Rahman", new DateTime(2026, 9, 8, 10, 13, 0)),
                    new TrackedChange(step5, "22 N·m", "25 N·m", "Nadia Rahman", new DateTime(2026, 9, 8, 10, 15, 0)),
                ],
                Comments = [new ReviewComment(step5, "Nadia Rahman", "Fabrikam Seals confirmed 25 N·m for the PN-4471-C gland nuts.", new DateTime(2026, 9, 8, 10, 16, 0))],
                Properties = new Dictionary<string, string> { ["EffectiveDate"] = "2026-09-14", ["DocumentOwner"] = "Nadia Rahman", ["Revision"] = "3" },
            },
            Note = "Current revision. The file still carries tracked changes from revision 2 (the old part number and torque are deleted text) and a reviewer's comment.",
        };

        var rev2 = new DocBuilder("SOP-FS-014", "2", Of(
            "SOP-FS-014: Mechanical seal replacement on CP-200 pumps", "fluid", DocumentTypes.Sop, SensitivityLabels.Internal,
            FluidAndService, "Nadia Rahman", revision: "2", from: D(2024, 3, 4), to: D(2026, 9, 13),
            supersedes: ["SOP-FS-014 rev 1"], tags: ["CP-200", "P-201"], parts: ["PN-4471-B"]));
        rev2.H(1, "SOP-FS-014: Mechanical seal replacement on CP-200 pumps")
            .P("Revision 2, effective March 4, 2024. This revision adds the zero-energy check required by safety bulletin SB-0932.")
            .Steps(
                "Apply lockout/tagout to the motor and isolate the seal-water and process lines, as safety bulletin SB-0932 requires. Verify zero energy.",
                "Drain the casing and remove the coupling guard.",
                "Remove the seal housing and the old seal. Inspect the shaft sleeve for scoring.",
                "Fit mechanical seal PN-4471-B.",
                "Tighten the seal gland nuts evenly to 22 N·m.",
                "Refit the coupling guard, remove the locks and tags, and start the pump as described in section 5.4 of the manual.",
                "Record the seal's part number and the operating hours in the work order.")
            .H(2, "Purpose and scope").P(purpose).H(2, "Tools and parts").Bullets(tools)
            .H(2, "Records").P("Record the seal's part number, the pump's operating hours and the names of the people who did the work in the work order.");
        yield return new SourceSpec(rev2.Build("fileshare", "fileshare/fluid/sops/archive/SOP-FS-014 Mechanical seal replacement Rev 2.docx"), Format.Docx, new DateTimeOffset(2024, 2, 27, 11, 0, 0, TimeSpan.Zero))
        {
            Author = "Nadia Rahman",
            Docx = new DocxOptions
            {
                Header = "SOP-FS-014 · Revision 2 · Superseded",
                Footer = "Uncontrolled when printed. Check the SOP library for the current revision.",
                Properties = new Dictionary<string, string> { ["EffectiveDate"] = "2024-03-04", ["DocumentOwner"] = "Nadia Rahman", ["Revision"] = "2" },
            },
            Note = "Superseded revision kept in the archive folder: PN-4471-B and 22 N·m.",
        };

        // The intranet publishes the current SOP as a web page: the same text in another format.
        yield return new SourceSpec(rev3Truth with
        {
            Id = "INTRANET-SOP-FS-014",
            Version = "3",
            Source = rev3Truth.Source with { System = "intranet", Uri = "intranet/procedures/sop-fs-014.html", MediaType = MediaTypes.Html },
        }, Format.Html, new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.Zero))
        {
            Html = new HtmlPage("Engineering", "Home › Engineering › Procedures › SOP-FS-014", [("/procedures/sop-fs-007.html", "SOP-FS-007: Pump isolation and lockout"), ("/safety/sb-0932.html", "SB-0932: Seal housing work")])
            {
                MetaTags = new Dictionary<string, string> { ["contoso:doc-id"] = "SOP-FS-014", ["contoso:revision"] = "3", ["contoso:effective"] = "2026-09-14" },
            },
            Note = "The intranet's rendering of SOP-FS-014 revision 3: a near-duplicate of the Word file in another format.",
        };
    }

    private static SourceSpec IsolationSop()
    {
        var b = new DocBuilder("SOP-FS-007", "5", Of(
            "SOP-FS-007: Pump isolation and lockout", "fluid", DocumentTypes.Sop, SensitivityLabels.Internal,
            FluidAndService, "Nadia Rahman", revision: "5", from: D(2025, 5, 12), tags: ["CP-200", "V-114"]));
        b.H(1, "SOP-FS-007: Pump isolation and lockout")
         .P("Revision 5, effective May 12, 2025. This procedure isolates a pump so that it can't start, can't be pressurized and can't move while people work on it. It implements safety bulletin SB-0932 for Fluid Systems pumps.")
         .H(2, "Isolation steps")
         .Steps(
            "Tell the control room which pump you are isolating and why.",
            "Stop the pump from the local panel and open the motor's isolator.",
            "Fit your own padlock and tag to the isolator. Everyone who works on the pump fits their own lock.",
            "Close the suction and discharge valves and lock them closed.",
            "Close the seal-water isolation valve upstream of the strainer and open the blow-down valve.",
            "Open the casing drain and wait until it stops flowing.",
            "Try to start the pump from the local panel to prove that it can't start, then return the switch to off.",
            "Check that every pressure gauge on the pump reads zero.")
         .H(2, "Removing the isolation")
         .P("Remove the isolation in the reverse order once everyone has removed their lock. The last person to remove a lock checks that the coupling guard is fitted and that no tools are left on the skid.")
         .H(2, "Seal-water valves at Riverside")
         .P("At the Riverside plant, the seal-water valve for each pump carries the pump's tag with the prefix V: valve V-114 feeds pump P-201. Don't close V-114 itself for isolation; close the isolation valve upstream of the strainer, so that V-114's setting is kept.");
        return new SourceSpec(b.Build("fileshare", "fileshare/fluid/sops/SOP-FS-007 Pump isolation and lockout.docx"), Format.Docx, new DateTimeOffset(2025, 5, 6, 9, 30, 0, TimeSpan.Zero))
        {
            Author = "Nadia Rahman",
            Docx = new DocxOptions { Header = "SOP-FS-007 · Revision 5 · Controlled copy", Footer = "Uncontrolled when printed.", Properties = new Dictionary<string, string> { ["EffectiveDate"] = "2025-05-12", ["Revision"] = "5" } },
        };
    }

    private static SourceSpec StrainerInstruction()
    {
        var b = new DocBuilder("WI-FS-031", "1", Of(
            "WI-FS-031: Cleaning the seal-water strainer upstream of V-114", "fluid", DocumentTypes.WorkInstruction, SensitivityLabels.Internal,
            ["fluid"], "Riverside maintenance", revision: "1", from: D(2025, 11, 3), tags: ["P-201", "V-114"]));
        b.Seed("wi-fs-031");
        return new SourceSpec(b.Build("fileshare", "fileshare/fluid/work-instructions/WI-FS-031 Seal-water strainer cleaning.docx"), Format.Docx, new DateTimeOffset(2025, 10, 30, 16, 0, 0, TimeSpan.Zero))
        {
            Author = "Tomás Silva",
            Docx = new DocxOptions { Header = "WI-FS-031 · Riverside plant", Properties = new Dictionary<string, string> { ["EffectiveDate"] = "2025-11-03" } },
        };
    }

    private static SourceSpec Ecn2291()
    {
        var b = new DocBuilder("ECN-2291", "1", Of(
            "Engineering change notice ECN-2291: CP-200 mechanical seal", "fluid", DocumentTypes.ChangeNotice, SensitivityLabels.Internal,
            ["fluid"], "Nadia Rahman", from: D(2026, 9, 14), supersedes: ["PN-4471-B"], tags: ["CP-200"], parts: ["PN-4471-B", "PN-4471-C"]));
        b.H(1, "Engineering change notice ECN-2291")
         .Table(null, ["Field", "Entry"],
            ["Change notice", "ECN-2291"],
            ["Title", "CP-200 mechanical seal"],
            ["Product", "CP-200 centrifugal pump, all sizes"],
            ["Originator", "Nadia Rahman, reliability engineering"],
            ["Date raised", "August 21, 2026"],
            ["Effective", "September 14, 2026"])
         .Seed("ecn-2291", skipTitle: true)
         .Table("Affected parts", ["Old part", "New part", "Disposition"],
            ["PN-4471-B", "PN-4471-C", "Quarantine remaining stock; return to Fabrikam Seals for credit"])
         .P("Distribution: Fluid Systems maintenance planners at all four plants, Field Services, Procurement and the stores.");
        return new SourceSpec(b.Build("fileshare", "fileshare/fluid/changes/ECN-2291.pdf"), Format.Pdf, new DateTimeOffset(2026, 9, 11, 7, 45, 0, TimeSpan.Zero))
        {
            Author = "Document control",
            Page = PageStyle.Default with { RunningHeader = "Contoso Industrial · Engineering change notice", RunningFooter = "Form ENG-07 rev 4 · Page {page} of {pages}" },
            ScannedAll = true,
            Scan = ScanStyle.Default with
            {
                SkewDegrees = 1.8,
                Marks =
                [
                    new PaperMark(1, "APPROVED\n2026-09-10", 400, 92, 7, Rgb.Red),
                    new PaperMark(1, "NR", 470, 640, -8, Rgb.Blue, Stamp: false, Size: 22),
                    new PaperMark(1, "RECEIVED DOC CONTROL\nSEP 11 2026", 70, 760, -3, Rgb.Blue, Size: 11),
                ],
            },
            Note = "Signed paper form, scanned: a stamp across the header, handwritten initials and a received stamp.",
        };
    }

    private static SourceSpec Ecn2240()
    {
        var b = new DocBuilder("ECN-2240", "1", Of(
            "Engineering change notice ECN-2240: CP-200 casing wear ring material", "fluid", DocumentTypes.ChangeNotice, SensitivityLabels.Internal,
            ["fluid"], "Nadia Rahman", from: D(2025, 3, 1), supersedes: ["PN-2840-A"], tags: ["CP-200"], parts: ["PN-2840-A", "PN-2840-B"]));
        b.H(1, "Engineering change notice ECN-2240")
         .Table(null, ["Field", "Entry"],
            ["Change notice", "ECN-2240"],
            ["Title", "CP-200 casing wear ring material"],
            ["Product", "CP-200 centrifugal pump, all sizes"],
            ["Originator", "Nadia Rahman, reliability engineering"],
            ["Date raised", "January 27, 2025"],
            ["Effective", "March 1, 2025"])
         .P("Effective March 1, 2025, casing wear ring PN-2840-A (bronze) is superseded by PN-2840-B (carbon-filled PTFE composite) on all CP-200 pumps. The composite ring tolerates brief dry running without seizing, and its smaller running clearance of 0.25 mm improves efficiency by about 2 percent.")
         .P("Fit PN-2840-B whenever a wear ring is replaced. The bronze rings in stock may be used up on pumps that handle liquids above 80 °C, where the composite ring is not approved.")
         .Table("Affected parts", ["Old part", "New part", "Disposition"],
            ["PN-2840-A", "PN-2840-B", "Use up stock on pumps above 80 °C only"])
         .P("Affected documents: CP-200 manual (2009) sections 6.2 and 8.1.");
        return new SourceSpec(b.Build("fileshare", "fileshare/fluid/changes/ECN-2240.pdf"), Format.Pdf, new DateTimeOffset(2025, 2, 24, 13, 20, 0, TimeSpan.Zero))
        {
            Author = "Document control",
            Page = PageStyle.Default with { RunningHeader = "Contoso Industrial · Engineering change notice", RunningFooter = "Form ENG-07 rev 4 · Page {page} of {pages}" },
            ScannedAll = true,
            Scan = ScanStyle.Default with
            {
                SkewDegrees = 0.9,
                Noise = 1.0,
                Marks = [new PaperMark(1, "APPROVED\n2025-02-20", 410, 92, 5, Rgb.Red), new PaperMark(1, "NR", 470, 560, -10, Rgb.Blue, Stamp: false, Size: 22)],
            },
        };
    }

    private static SourceSpec TrainingDeck()
    {
        SlideSpec[] slides =
        [
            new("Replacing the CP-200 mechanical seal", ["Fluid Systems maintenance training", "Revised for ECN-2291, October 2026"],
                "Welcome the group and ask who has replaced a CP-200 seal before. This course follows SOP-FS-014 revision 3."),
            new("Why seals fail", ["Dry running, even for a few seconds", "Seal-water pressure below 1.5 bar (error E-217 trips the pump)", "Abrasive particles in the seal water", "Carbon face wear above 60 °C seal-water temperature"],
                "In 2026, three PN-4471-B failures at Riverside were traced to carbon face wear at seal-water temperatures above 60 °C. That is why ECN-2291 introduced PN-4471-C with a silicon-carbide face."),
            new("Before you start", ["Lockout/tagout as SB-0932 and SOP-FS-007 require", "Verify zero energy", "Everyone fits their own lock"],
                "Zero energy means that the motor can't start from the panel, the local pressure gauges read zero and the casing drain is open."),
            new("Parts and tools", ["Seal cartridge PN-4471-C", "New casing gaskets PN-2205-C", "Torque wrench, 5 to 60 N·m"],
                "Check the part number on the box. Remaining PN-4471-B stock is quarantined; never fit it."),
            new("Removing the old seal", ["Drain the casing and remove the coupling guard", "Remove the seal housing and the old seal", "Inspect the shaft sleeve for scoring"],
                "Replace a scored shaft sleeve; a new seal won't seal on it."),
            new("Fitting PN-4471-C", ["Fit the cartridge without tilting it", "Tighten the gland nuts evenly to 25 N·m", "Don't mix PN-4471-B and PN-4471-C parts"],
                "The old torque of 22 N·m applied to PN-4471-B. Fabrikam Seals confirmed 25 N·m for PN-4471-C."),
            new("Restarting the pump", ["Vent the casing", "Open the seal-water supply: 2.0 to 3.0 bar at the flush connection", "Start as section 5.4 of the manual describes"],
                "Watch the seal for the first hour. A few drops while the faces run in are normal."),
            new("Records", ["Seal part number and operating hours in the work order", "Return failed seals for analysis"],
                "Questions about the course go to Nadia Rahman in reliability engineering."),
        ];

        var b = new DocBuilder("TRN-FS-2026-04", "1", Of(
            "Replacing the CP-200 mechanical seal (training)", "fluid", DocumentTypes.Training, SensitivityLabels.Internal,
            FluidAndService, "Nadia Rahman", from: D(2026, 10, 5), tags: ["CP-200"], parts: ["PN-4471-C", "PN-2205-C"]));
        for (var i = 0; i < slides.Length; i++)
        {
            var slide = slides[i];
            b.Add(new HeadingBlock(i == 0 ? 1 : 2, slide.Title) { Anchor = new SourceAnchor { Path = $"slide {i + 1}" } });
            foreach (var bullet in slide.Bullets)
            {
                b.Add(new ListItemBlock(bullet) { Anchor = new SourceAnchor { Path = $"slide {i + 1}" } });
            }

            if (slide.Notes is not null)
            {
                b.Add(new ParagraphBlock(slide.Notes) { Role = BlockRoles.SpeakerNotes, Anchor = new SourceAnchor { Path = $"slide {i + 1}/notes" } });
            }
        }

        return new SourceSpec(b.Build("fileshare", "fileshare/fluid/training/CP-200 seal replacement training.pptx"), Format.Pptx, new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.Zero))
        {
            Author = "Nadia Rahman",
            Slides = slides,
            Note = "A deck whose most useful facts are in the speaker notes.",
        };
    }

    private static IEnumerable<SourceSpec> AssetPages()
    {
        var p201 = new DocBuilder("P201-ASSET", "2", Of(
            "Asset record: pump P-201", "fluid", DocumentTypes.AssetRecord, SensitivityLabels.Internal,
            FluidAndService, "Riverside maintenance", revision: "2", from: D(2026, 11, 19),
            supersedes: ["P201-ASSET rev 1"], tags: ["P-201", "V-114", "CP-200"], parts: ["PN-4471-C"]));
        p201.Seed("p201-asset");
        yield return AssetPage(p201.Build("intranet", "intranet/assets/p-201.html"), new DateTimeOffset(2026, 11, 19, 17, 2, 0, TimeSpan.Zero), "P-201");

        var p202 = new DocBuilder("P202-ASSET", "1", Of(
            "Asset record: pump P-202", "fluid", DocumentTypes.AssetRecord, SensitivityLabels.Internal,
            FluidAndService, "Riverside maintenance", revision: "1", from: D(2019, 8, 2), tags: ["P-202", "V-115", "CP-200"], parts: ["PN-4471-B"]));
        p202.H(1, "Asset record: pump P-202")
            .Table(null, ["Field", "Value"],
                ["Tag", "P-202"], ["Model", "CP-200 centrifugal pump"], ["Site", "Riverside plant, cooling-water building"],
                ["Duty", "Cooling-water transfer, standby for P-201"], ["Motor", "30 kW, 1,480 rpm"], ["Commissioned", "May 2011"],
                ["Seal-water valve", "V-115"], ["Mechanical seal", "PN-4471-B, fitted August 2019; replace with PN-4471-C at the next seal change (ECN-2291)"])
            .P("P-202 is the standby pump for P-201 and runs about 1,200 hours a year, mostly while P-201 is maintained. Its manual is the CP-200 Centrifugal Pump Manual (2009).");
        yield return AssetPage(p202.Build("intranet", "intranet/assets/p-202.html"), new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), "P-202");

        var p301 = new DocBuilder("P301-ASSET", "1", Of(
            "Asset record: pump P-301", "fluid", DocumentTypes.AssetRecord, SensitivityLabels.Internal,
            FluidAndService, "Northgate maintenance", revision: "1", from: D(2026, 12, 3), tags: ["P-301", "CP-200"], parts: ["PN-4471-C"]));
        p301.H(1, "Asset record: pump P-301")
            .Table(null, ["Field", "Value"],
                ["Tag", "P-301"], ["Model", "CP-200 centrifugal pump"], ["Site", "Northgate plant, process water"],
                ["Duty", "Process-water supply, 120 m³/h at 40 m head"], ["Motor", "22 kW, 1,480 rpm"], ["Commissioned", "March 2015"],
                ["Seal-water valve", "V-311"], ["Mechanical seal", "PN-4471-C since work order WO-88454 (December 2026)"])
            .P("P-301 runs about 6,000 hours a year. Its seal water comes from the plant's softened-water ring main, so its strainer rarely blocks.");
        yield return AssetPage(p301.Build("intranet", "intranet/assets/p-301.html"), new DateTimeOffset(2026, 12, 3, 10, 30, 0, TimeSpan.Zero), "P-301");
    }

    private static SourceSpec AssetPage(CanonicalDocument truth, DateTimeOffset modified, string tag) =>
        new(truth, Format.Html, modified)
        {
            Html = new HtmlPage("Engineering", $"Home › Engineering › Assets › {tag}", [("/assets/p-201.html", "P-201"), ("/assets/p-202.html", "P-202"), ("/assets/p-301.html", "P-301")])
            {
                MetaTags = new Dictionary<string, string> { ["contoso:doc-id"] = truth.Id, ["contoso:revision"] = truth.Version, ["contoso:effective"] = $"{truth.Metadata.EffectiveFrom:yyyy-MM-dd}" },
            },
        };

    internal static int IndexOf(CanonicalDocument document, string text)
    {
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            if (document.Blocks[i].ToMarkdown().Contains(text, StringComparison.Ordinal)) return i;
        }

        throw new InvalidOperationException($"No block in {document.Id} contains \"{text}\".");
    }

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
