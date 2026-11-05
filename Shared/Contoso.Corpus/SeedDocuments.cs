namespace Contoso.Corpus;

/// <summary>
/// Six Contoso documents, as plain text, for the early chapters. Later chapters generate the full
/// corpus, in its real formats, from these and many more.
/// </summary>
public static class SeedDocuments
{
    public static SourceDocument PumpManualTorque { get; } = new(
        "CP200-MAN-2009-6.3",
        "CP-200 Centrifugal Pump Manual (2009), section 6.3: Flange bolt torque",
        "fluid", ["fluid", "service"],
        "Table 6-2 gives the tightening torque for the CP-200 flange bolts. " +
        "Tighten the M12 flange bolts (property class 8.8, lightly oiled) to 85 N·m. " +
        "Apply the torque in a cross pattern in three passes: 30 N·m, 60 N·m, then 85 N·m. " +
        "Re-check the torque after the first 24 hours of operation.");

    public static SourceDocument PumpManualService { get; } = new(
        "CP200-MAN-2009-7.2",
        "CP-200 Centrifugal Pump Manual (2009), section 7.2: Service intervals",
        "fluid", ["fluid", "service"],
        "Inspect the mechanical seal and the bearings every 2,000 operating hours or six months, whichever comes first. " +
        "Replace mechanical seal PN-4471-B every 8,000 operating hours.");

    public static SourceDocument SealChangeNotice { get; } = new(
        "ECN-2291",
        "Engineering change notice ECN-2291: CP-200 mechanical seal",
        "fluid", ["fluid"],
        "Effective September 14, 2026, mechanical seal PN-4471-B is superseded by PN-4471-C on all CP-200 pumps. " +
        "PN-4471-C has a silicon-carbide face rated for higher seal-water temperatures. " +
        "Fit PN-4471-C at the next seal replacement and quarantine remaining PN-4471-B stock.");

    public static SourceDocument ErrorE217 { get; } = new(
        "CP200-TRB-E217",
        "CP-200 troubleshooting: error E-217",
        "fluid", ["fluid", "service"],
        "Error E-217 means seal-water pressure low. " +
        "The controller trips the pump when seal-water pressure stays below 1.5 bar for 10 seconds, to protect the mechanical seal. " +
        "Check that seal-water valve V-114 is open and that its strainer is clean, then reset the alarm at the local panel.");

    public static SourceDocument SafetyBulletin { get; } = new(
        "SB-0932",
        "Safety bulletin SB-0932: Seal housing work",
        ContosoFacts.CorporateLibrary, ["employees"],
        "Apply lockout/tagout (LOTO) to the motor and isolate the seal-water and process lines before opening any CP-200 seal housing. " +
        "Verify zero energy before work starts.");

    public static SourceDocument ParentalLeaveGuidance { get; } = new(
        "HR-PL-2027",
        "HR guidance: parental leave changes for 2027 (restricted until announced)",
        ContosoFacts.CorporateLibrary, ["hr", "people-managers"],
        "This guidance is restricted to HR and people managers until the change is announced on February 15, 2027. " +
        "From March 1, 2027, paid parental leave increases from 16 to 20 weeks and can be taken in up to three blocks.");

    public static IReadOnlyList<SourceDocument> All { get; } =
        [PumpManualTorque, PumpManualService, SealChangeNotice, ErrorE217, SafetyBulletin, ParentalLeaveGuidance];
}
