using System.Security;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Contoso.Corpus.Generator.Writers;

/// <summary>One slide: a title, bullet points and the presenter's notes.</summary>
internal sealed record SlideSpec(string Title, IReadOnlyList<string> Bullets, string? Notes);

/// <summary>
/// Writes a PowerPoint deck with title and body placeholders and speaker notes. The parts are written as
/// compact XML: a valid deck needs a master, a layout, a theme and a notes master, which typed code makes long.
/// </summary>
internal static class PptxWriter
{
    private const string Ns =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    private const string GroupProps =
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";

    private const string ColorMap =
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" " +
        "accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";

    public static byte[] Write(string title, string author, DateTime created, IReadOnlyList<SlideSpec> slides)
    {
        using var stream = new MemoryStream();
        using (var deck = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            deck.PackageProperties.Title = title;
            deck.PackageProperties.Creator = author;
            deck.PackageProperties.Created = created;
            deck.PackageProperties.Modified = created;

            var presentation = deck.AddPresentationPart();
            var master = presentation.AddNewPart<SlideMasterPart>("rIdMaster");
            var layout = master.AddNewPart<SlideLayoutPart>("rIdLayout1");
            Feed(layout, $"<p:sldLayout {Ns} type=\"obj\" preserve=\"1\"><p:cSld name=\"Title and Content\"><p:spTree>{GroupProps}" +
                Placeholder(2, "Title 1", "type=\"title\"", 838200, 365125, 10515600, 1325563, null) +
                Placeholder(3, "Content Placeholder 2", "idx=\"1\"", 838200, 1825625, 10515600, 4351338, null) +
                "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
            var theme = master.AddNewPart<ThemePart>("rIdTheme");
            Feed(theme, Theme("Contoso"));
            layout.AddPart(master, "rIdMaster");
            Feed(master, $"<p:sldMaster {Ns}><p:cSld><p:spTree>{GroupProps}" +
                Placeholder(2, "Title Placeholder 1", "type=\"title\"", 838200, 365125, 10515600, 1325563, null) +
                Placeholder(3, "Text Placeholder 2", "type=\"body\" idx=\"1\"", 838200, 1825625, 10515600, 4351338, null) +
                $"</p:spTree></p:cSld>{ColorMap}<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rIdLayout1\"/></p:sldLayoutIdLst></p:sldMaster>");

            var notesMaster = presentation.AddNewPart<NotesMasterPart>("rIdNotesMaster");
            Feed(notesMaster.AddNewPart<ThemePart>("rIdTheme"), Theme("Notes"));
            Feed(notesMaster, $"<p:notesMaster {Ns}><p:cSld><p:spTree>{GroupProps}" +
                Placeholder(2, "Notes Placeholder 1", "type=\"body\" idx=\"1\"", 685800, 4400550, 5486400, 3600450, null) +
                $"</p:spTree></p:cSld>{ColorMap}</p:notesMaster>");

            var slideIds = new StringBuilder();
            for (var i = 0; i < slides.Count; i++)
            {
                var slide = slides[i];
                var part = presentation.AddNewPart<SlidePart>($"rIdSlide{i + 1}");
                part.AddPart(layout, "rIdLayout1");
                Feed(part, $"<p:sld {Ns}><p:cSld><p:spTree>{GroupProps}" +
                    Placeholder(2, "Title 1", "type=\"title\"", null, null, null, null, [slide.Title]) +
                    Placeholder(3, "Content Placeholder 2", "idx=\"1\"", null, null, null, null, slide.Bullets) +
                    "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
                if (slide.Notes is not null)
                {
                    var notes = part.AddNewPart<NotesSlidePart>("rIdNotes");
                    notes.AddPart(part, "rIdSlide");
                    notes.AddPart(notesMaster, "rIdNotesMaster");
                    Feed(notes, $"<p:notes {Ns}><p:cSld><p:spTree>{GroupProps}" +
                        Placeholder(2, "Notes Placeholder 1", "type=\"body\" idx=\"1\"", null, null, null, null, [slide.Notes]) +
                        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
                }

                slideIds.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rIdSlide{i + 1}\"/>");
            }

            Feed(presentation, $"<p:presentation {Ns} saveSubsetFonts=\"1\">" +
                "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rIdMaster\"/></p:sldMasterIdLst>" +
                "<p:notesMasterIdLst><p:notesMasterId r:id=\"rIdNotesMaster\"/></p:notesMasterIdLst>" +
                $"<p:sldIdLst>{slideIds}</p:sldIdLst>" +
                "<p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
        }

        return Zip.Normalize(stream.ToArray(), created);
    }

    private static string Placeholder(int id, string name, string ph, long? x, long? y, long? cx, long? cy, IReadOnlyList<string>? paragraphs)
    {
        var geometry = x is null ? "<p:spPr/>" :
            $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm></p:spPr>";
        var text = paragraphs is null ? "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>" :
            string.Concat(paragraphs.Select(t => $"<a:p><a:r><a:rPr lang=\"en-US\" dirty=\"0\"/><a:t>{SecurityElement.Escape(t)}</a:t></a:r></a:p>"));
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
            $"<p:nvPr><p:ph {ph}/></p:nvPr></p:nvSpPr>{geometry}<p:txBody><a:bodyPr/><a:lstStyle/>{text}</p:txBody></p:sp>";
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var data = new MemoryStream(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml));
        part.FeedData(data);
    }

    private static string Theme(string name)
    {
        static string Solid(string scheme) => $"<a:solidFill><a:schemeClr val=\"{scheme}\"/></a:solidFill>";
        static string Line(int w) => $"<a:ln w=\"{w}\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>";
        return "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"" + name + "\"><a:themeElements>" +
            "<a:clrScheme name=\"Office\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
            "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"0078D4\"/></a:accent1>" +
            "<a:accent2><a:srgbClr val=\"107C10\"/></a:accent2><a:accent3><a:srgbClr val=\"FFB900\"/></a:accent3><a:accent4><a:srgbClr val=\"D13438\"/></a:accent4>" +
            "<a:accent5><a:srgbClr val=\"5C2D91\"/></a:accent5><a:accent6><a:srgbClr val=\"FF8C00\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink>" +
            "<a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
            "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
            "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
            "<a:fmtScheme name=\"Office\"><a:fillStyleLst>" + Solid("phClr") + Solid("phClr") + Solid("phClr") + "</a:fillStyleLst>" +
            "<a:lnStyleLst>" + Line(6350) + Line(12700) + Line(19050) + "</a:lnStyleLst>" +
            "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
            "<a:bgFillStyleLst>" + Solid("phClr") + Solid("phClr") + Solid("phClr") + "</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>";
    }
}
