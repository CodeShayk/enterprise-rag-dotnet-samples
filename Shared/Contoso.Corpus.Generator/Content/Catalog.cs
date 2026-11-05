using Contoso.Corpus.Generator.Writers;
using Contoso.Knowledge.Documents;
using static Contoso.Corpus.Generator.Content.Meta;

namespace Contoso.Corpus.Generator.Content;

/// <summary>The 2027 parts catalog: one sheet per business, merged group headers, price formulas and a superseded-parts sheet.</summary>
internal static class Catalog
{
    private static readonly string[] Header =
        ["Part number", "Description", "Fits", "Supplier", "Lead time (weeks)", "Stock at Riverside", "List price", "Discount", "Net price"];

    private static readonly (string Text, int From, int To)[] Groups = [("Part", 0, 2), ("Supply", 3, 5), ("Price (USD)", 6, 8)];

    private static readonly object[][] FluidParts =
    [
        ["PN-4471-B", "Mechanical seal cartridge, carbon face (superseded by PN-4471-C, ECN-2291)", "CP-200", "Fabrikam Seals", 6, 0, 1280.0, 0.08],
        ["PN-4471-C", "Mechanical seal cartridge, silicon-carbide face", "CP-200", "Fabrikam Seals", 6, 7, 1450.0, 0.08],
        ["PN-3310-A", "Ball bearing 6309-2Z/C3, pump end", "CP-200", "Litware Bearings", 2, 12, 86.0, 0.10],
        ["PN-3311-A", "Ball bearing 6311-2Z/C3, drive end", "CP-200", "Litware Bearings", 2, 10, 112.0, 0.10],
        ["PN-3420-A", "Grease, lithium-complex NLGI 2, 400 g cartridge", "CP-200", "Litware Bearings", 1, 60, 9.8, 0.0],
        ["PN-2205-C", "Casing gasket, spiral-wound", "CP-200", "Fabrikam Seals", 3, 40, 38.5, 0.05],
        ["PN-2840-A", "Casing wear ring, bronze (superseded by PN-2840-B, ECN-2240)", "CP-200", "Proseware Castings", 8, 3, 210.0, 0.0],
        ["PN-2840-B", "Casing wear ring, carbon-filled PTFE", "CP-200", "Proseware Castings", 5, 9, 265.0, 0.05],
        ["PN-2910-A", "Impeller, 245 mm, duplex stainless steel", "CP-200", "Proseware Castings", 10, 2, 1860.0, 0.05],
        ["PN-2911-A", "Impeller, 265 mm, duplex stainless steel", "CP-200", "Proseware Castings", 10, 1, 1990.0, 0.05],
        ["PN-2950-A", "Casing, cast iron, complete with wear ring seat", "CP-200", "Proseware Castings", 16, 0, 4200.0, 0.05],
        ["PN-8031-A", "Y-strainer screen, 100 micron", "Seal-water lines (V-114 and similar)", "Fabrikam Seals", 2, 18, 42.0, 0.05],
        ["PN-8032-A", "Y-strainer cap gasket", "Seal-water lines (V-114 and similar)", "Fabrikam Seals", 2, 30, 4.5, 0.0],
        ["PN-8110-A", "Pressure switch PS-31, 0.5 to 6 bar", "CP-200 seal water", "Contoso stores", 3, 4, 155.0, 0.0],
        ["PN-8120-A", "Pressure transmitter PT-11, 4 to 20 mA", "CP-200 suction", "Contoso stores", 4, 3, 380.0, 0.0],
        ["PN-8130-A", "Bearing temperature sensor Pt100 (TE-21, TE-22)", "CP-200", "Contoso stores", 2, 8, 95.0, 0.0],
        ["PN-8140-A", "Controller clock battery CR2032", "CP-200 controller", "Contoso stores", 1, 50, 2.1, 0.0],
    ];

    private static readonly object[][] MotionParts =
    [
        ["PN-6120-A", "Breather plug, open (superseded by PN-6120-B, ECN-2305)", "HG-400", "Proseware Castings", 2, 4, 18.0, 0.0],
        ["PN-6120-B", "Breather plug with desiccant filter", "HG-400", "Proseware Castings", 3, 12, 46.0, 0.05],
        ["PN-6125-A", "Desiccant cartridge for PN-6120-B", "HG-400", "Proseware Castings", 2, 30, 12.0, 0.0],
        ["PN-6210-A", "Shaft seal, input, 55 x 80 x 10 mm", "HG-400", "Fabrikam Seals", 2, 10, 24.0, 0.05],
        ["PN-6211-A", "Shaft seal, output, 90 x 120 x 12 mm", "HG-400", "Fabrikam Seals", 2, 8, 39.0, 0.05],
        ["PN-6310-A", "Tapered roller bearing, input shaft", "HG-400", "Litware Bearings", 4, 4, 145.0, 0.10],
        ["PN-6311-A", "Tapered roller bearing, output shaft", "HG-400", "Litware Bearings", 4, 4, 236.0, 0.10],
        ["PN-6410-A", "Gear oil ISO VG 220, mineral, 20 l", "HG-400", "Contoso stores", 1, 25, 96.0, 0.0],
        ["PN-6411-A", "Gear oil ISO VG 220, synthetic PAO, 20 l", "HG-400", "Contoso stores", 1, 10, 248.0, 0.0],
        ["PN-6412-A", "Gear oil ISO VG 220, food-grade NSF H1, 20 l", "HG-400", "Contoso stores", 2, 6, 312.0, 0.0],
        ["PN-5120-A", "Elastomer element, RX coupling size 50", "RX 50", "Contoso stores", 1, 15, 64.0, 0.0],
        ["PN-5170-A", "Elastomer element, RX coupling size 70", "RX 70", "Contoso stores", 1, 9, 88.0, 0.0],
        ["PN-5190-A", "Elastomer element, RX coupling size 90", "RX 90", "Contoso stores", 2, 6, 121.0, 0.0],
        ["PN-5210-A", "Elastomer element, RX coupling size 110", "RX 110", "Contoso stores", 3, 2, 176.0, 0.0],
        ["PN-7716-A", "Cooling fan, VD-75 frame C", "VD-75", "Contoso stores", 3, 5, 74.0, 0.0],
        ["PN-7720-A", "Fuse kit, VD-75", "VD-75", "Contoso stores", 1, 10, 33.0, 0.0],
    ];

    public static SourceSpec PartsCatalog()
    {
        var sheets = new List<SheetSpec>
        {
            Sheet("Fluid Systems", "Parts catalog 2027: Fluid Systems", FluidParts, "FluidNetPrices"),
            Sheet("Motion Systems", "Parts catalog 2027: Motion Systems", MotionParts, "MotionNetPrices"),
            new("Superseded", "Superseded parts", [("Change", 0, 4)], ["Old part", "Replaced by", "Change notice", "Effective", "Instruction"],
            [
                ["PN-2840-A", "PN-2840-B", "ECN-2240", "2025-03-01", "Use up bronze rings on pumps above 80 °C only"],
                ["PN-4471-B", "PN-4471-C", "ECN-2291", "2026-09-14", "Quarantine remaining stock"],
                ["PN-6120-A", "PN-6120-B", "ECN-2305", "2026-12-01", "Replace at the next oil change"],
            ]),
        };

        var truth = new DocBuilder("PARTS-CAT-2027", "2027-01", Of("Contoso parts catalog 2027", "corporate", DocumentTypes.PartsCatalog,
            SensitivityLabels.Internal, ["employees"], "Procurement", revision: "2027-01", from: D(2027, 1, 4),
            parts: [.. FluidParts.Concat(MotionParts).Select(r => (string)r[0])]));
        return new SourceSpec(truth.Build("fileshare", "fileshare/procurement/Parts Catalog 2027.xlsx"), Format.Xlsx, new DateTimeOffset(2027, 1, 4, 8, 0, 0, TimeSpan.Zero))
        {
            Author = "Grace Liu",
            Sheets = sheets,
            Note = "Merged title and group header rows, net prices as formulas with cached values, named ranges and a superseded-parts sheet.",
        };
    }

    private static SheetSpec Sheet(string name, string title, object[][] parts, string named) =>
        new(name, title, Groups, Header,
            [.. parts.Select((p, i) => p.Append($"=G{i + 4}*(1-H{i + 4})").ToArray())])
        {
            Formats = new Dictionary<int, string> { [6] = "#,##0.00", [7] = "0%", [8] = "#,##0.00" },
            NamedColumns = new Dictionary<string, int> { [named] = 8 },
        };
}
