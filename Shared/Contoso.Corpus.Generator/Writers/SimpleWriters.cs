using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using Contoso.Knowledge.Documents;
using MimeKit;

namespace Contoso.Corpus.Generator.Writers;

/// <summary>A worksheet: a merged title row, merged group headers over column headers, rows with values or formulas.</summary>
internal sealed record SheetSpec(string Name, string Title, IReadOnlyList<(string Text, int From, int To)> Groups,
    IReadOnlyList<string> Header, IReadOnlyList<object[]> Rows)
{
    /// <summary>Excel number formats by zero-based column, such as <c>0.00</c>.</summary>
    public IReadOnlyDictionary<int, string> Formats { get; init; } = new Dictionary<int, string>();

    /// <summary>Named ranges over whole data columns, by zero-based column.</summary>
    public IReadOnlyDictionary<string, int> NamedColumns { get; init; } = new Dictionary<string, int>();
}

internal static class XlsxWriter
{
    /// <summary>Writes the sheets and returns the workbook plus each sheet as the table a perfect parser would recover.</summary>
    public static (byte[] Bytes, IReadOnlyList<Block> Truth) Write(IReadOnlyList<SheetSpec> sheets, string author, DateTime created)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Author = author;
        workbook.Properties.Created = created;
        workbook.Properties.Modified = created;
        var truth = new List<Block>();
        foreach (var spec in sheets)
        {
            var sheet = workbook.AddWorksheet(spec.Name);
            var columns = spec.Header.Count;
            sheet.Cell(1, 1).Value = spec.Title;
            sheet.Range(1, 1, 1, columns).Merge().Style.Font.SetBold().Font.SetFontSize(14);
            foreach (var (text, from, to) in spec.Groups)
            {
                sheet.Cell(2, from + 1).Value = text;
                sheet.Range(2, from + 1, 2, to + 1).Merge().Style.Font.SetBold().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            }

            for (var c = 0; c < columns; c++)
            {
                sheet.Cell(3, c + 1).Value = spec.Header[c];
                sheet.Cell(3, c + 1).Style.Font.SetBold();
            }

            for (var r = 0; r < spec.Rows.Count; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    var cell = sheet.Cell(r + 4, c + 1);
                    switch (spec.Rows[r][c])
                    {
                        case string s when s.StartsWith('='):
                            cell.FormulaA1 = s[1..];
                            break;
                        case string s:
                            cell.Value = s;
                            break;
                        case int i:
                            cell.Value = i;
                            break;
                        case double d:
                            cell.Value = d;
                            break;
                        case null:
                            break;
                    }

                    if (spec.Formats.TryGetValue(c, out var format)) cell.Style.NumberFormat.Format = format;
                }
            }

            foreach (var (name, column) in spec.NamedColumns)
            {
                workbook.DefinedNames.Add(name, sheet.Range(4, column + 1, spec.Rows.Count + 3, column + 1));
            }

            sheet.Columns().AdjustToContents();
            sheet.SheetView.FreezeRows(3);
        }

        // Store the formulas' results too, as Excel does; readers that don't evaluate formulas see only these.
        workbook.RecalculateAllFormulas();
        foreach (var spec in sheets)
        {
            var sheet = workbook.Worksheet(spec.Name);
            var groupRow = Enumerable.Range(0, spec.Header.Count)
                .Select(c => spec.Groups.FirstOrDefault(g => c >= g.From && c <= g.To).Text ?? "").ToList();
            var rows = new List<IReadOnlyList<string>> { groupRow, spec.Header };
            for (var r = 0; r < spec.Rows.Count; r++)
            {
                rows.Add([.. Enumerable.Range(0, spec.Header.Count).Select(c => sheet.Cell(r + 4, c + 1).GetFormattedString())]);
            }

            truth.Add(new HeadingBlock(2, spec.Title) { Anchor = new SourceAnchor { Path = $"sheet {spec.Name}!A1" } });
            truth.Add(new TableBlock(rows, HeaderRows: 2) { Anchor = new SourceAnchor { Path = $"sheet {spec.Name}!A2:{(char)('A' + spec.Header.Count - 1)}{spec.Rows.Count + 3}" } });
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return (Zip.Normalize(stream.ToArray(), created), truth);
    }
}

/// <summary>The intranet's page template: navigation, breadcrumb, related links and footer around the article.</summary>
internal sealed record HtmlPage(string Section, string Breadcrumb, IReadOnlyList<(string Href, string Text)> Related)
{
    public IReadOnlyDictionary<string, string> MetaTags { get; init; } = new Dictionary<string, string>();
}

internal static class HtmlWriter
{
    public static string Write(CanonicalDocument truth, HtmlPage page)
    {
        var html = new StringBuilder();
        html.Append($"<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>{E(truth.Metadata.Title)} | Contoso Intranet</title>\n");
        foreach (var (name, content) in page.MetaTags)
        {
            html.Append($"<meta name=\"{E(name)}\" content=\"{E(content)}\">\n");
        }

        html.Append("<link rel=\"stylesheet\" href=\"/assets/intranet.css\">\n</head>\n<body>\n");
        html.Append("<header class=\"site-header\">\n<a class=\"brand\" href=\"/\">Contoso Intranet</a>\n<nav aria-label=\"Main\"><ul>\n");
        foreach (var item in new[] { "Home", "Safety", "Engineering", "Field Services", "HR", "IT help" })
        {
            var active = item == page.Section ? " class=\"active\"" : "";
            html.Append($"<li{active}><a href=\"/{item.ToLowerInvariant().Replace(' ', '-')}/\">{item}</a></li>\n");
        }

        html.Append("</ul></nav>\n<form role=\"search\" action=\"/search\"><input name=\"q\" placeholder=\"Search the intranet\"><button>Search</button></form>\n</header>\n");
        html.Append($"<div class=\"breadcrumb\">{E(page.Breadcrumb)}</div>\n<main>\n<article>\n");
        var inList = false;
        foreach (var block in truth.Blocks)
        {
            var listItem = block is ListItemBlock;
            if (listItem && !inList) html.Append(((ListItemBlock)block).Marker is { } m && char.IsDigit(m[0]) ? "<ol>\n" : "<ul>\n");
            if (!listItem && inList) html.Append(html.ToString().LastIndexOf("<ol>", StringComparison.Ordinal) > html.ToString().LastIndexOf("<ul>", StringComparison.Ordinal) ? "</ol>\n" : "</ul>\n");
            inList = listItem;
            html.Append(block switch
            {
                HeadingBlock h => $"<h{h.Level}>{E(h.Text)}</h{h.Level}>\n",
                ParagraphBlock p => $"<p>{E(p.Text)}</p>\n",
                ListItemBlock li => $"<li>{E(li.Text)}</li>\n",
                TableBlock t => Table(t),
                _ => "",
            });
        }

        if (inList) html.Append("</ul>\n");
        html.Append("</article>\n<aside class=\"related\">\n<h2>Related</h2>\n<ul>\n");
        foreach (var (href, text) in page.Related)
        {
            html.Append($"<li><a href=\"{E(href)}\">{E(text)}</a></li>\n");
        }

        html.Append("</ul>\n</aside>\n</main>\n<footer class=\"site-footer\">\n<p>© 2027 Contoso Industrial. For internal use only.</p>\n");
        html.Append("<p><a href=\"/privacy/\">Privacy</a> · <a href=\"/accessibility/\">Accessibility</a> · Questions about this site? Contact the intranet team on extension 2200.</p>\n");
        html.Append("</footer>\n<script src=\"/assets/intranet.js\"></script>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static string Table(TableBlock t)
    {
        var html = new StringBuilder("<table>\n");
        if (t.Caption is not null) html.Append($"<caption>{E(t.Caption)}</caption>\n");
        for (var r = 0; r < t.Rows.Count; r++)
        {
            var tag = r < t.HeaderRows ? "th" : "td";
            html.Append("<tr>").Append(string.Concat(t.Rows[r].Select(c => $"<{tag}>{E(c)}</{tag}>"))).Append("</tr>\n");
        }

        return html.Append("</table>\n").ToString();
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}

/// <summary>One email in a thread.</summary>
internal sealed record MailMessageSpec(string From, string To, DateTimeOffset Date, string Subject, IReadOnlyList<string> Paragraphs)
{
    public string? Cc { get; init; }
}

/// <summary>A file attached to the newest message of a thread.</summary>
internal sealed record MailAttachment(string FileName, string MediaType, byte[] Content);

internal static class EmlWriter
{
    /// <summary>
    /// Writes the newest message of a thread with the earlier ones quoted below it, as mail clients do,
    /// and returns the file plus the truth: the new text as content and the history as quoted blocks.
    /// </summary>
    public static (byte[] Bytes, IReadOnlyList<Block> Truth) Write(IReadOnlyList<MailMessageSpec> thread, IReadOnlyList<MailAttachment> attachments)
    {
        var newest = thread[^1];
        var body = new StringBuilder();
        var truth = new List<Block>();
        foreach (var p in newest.Paragraphs)
        {
            body.Append(p).Append("\n\n");
            truth.Add(new ParagraphBlock(p));
        }

        // Each reply quotes everything before it, one more '>' per level.
        for (var i = thread.Count - 2; i >= 0; i--)
        {
            var depth = new string('>', thread.Count - 1 - i) + " ";
            var earlier = thread[i];
            var intro = $"On {earlier.Date.ToString("ddd, d MMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture)}, {earlier.From} wrote:";
            body.Append(depth.Length > 2 ? depth[..^2] + " " : "").Append(intro).Append('\n');
            truth.Add(new ParagraphBlock(intro) { Role = BlockRoles.Quote });
            foreach (var p in earlier.Paragraphs)
            {
                body.Append(depth).Append(p).Append('\n').Append(depth.TrimEnd()).Append('\n');
                truth.Add(new ParagraphBlock(p) { Role = BlockRoles.Quote });
            }
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(newest.From));
        message.To.Add(MailboxAddress.Parse(newest.To));
        if (newest.Cc is not null) message.Cc.Add(MailboxAddress.Parse(newest.Cc));
        message.Subject = newest.Subject;
        message.Date = newest.Date;
        message.MessageId = MessageId(newest);
        if (thread.Count > 1)
        {
            message.InReplyTo = MessageId(thread[^2]);
            foreach (var earlier in thread.Take(thread.Count - 1)) message.References.Add(MessageId(earlier));
        }

        var builder = new BodyBuilder { TextBody = body.ToString().TrimEnd() + "\n" };
        foreach (var attachment in attachments)
        {
            builder.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.MediaType));
        }

        message.Body = builder.ToMessageBody();
        // A fixed boundary keeps the file reproducible (with no HTML body, attachments sit in one multipart/mixed).
        if (message.Body is Multipart multipart) multipart.Boundary = "=-contoso-boundary-0";

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return (stream.ToArray(), truth);
    }

    // string.GetHashCode is randomized per process, so derive IDs from a real hash.
    private static string MessageId(MailMessageSpec m) =>
        $"{m.Date.ToUnixTimeSeconds()}.{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(m.From)))[..10]}@mail.contoso.example";
}

internal static class CsvWriter
{
    public static string Write(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var csv = new StringBuilder();
        csv.Append(string.Join(',', header.Select(Quote))).Append("\r\n");
        foreach (var row in rows)
        {
            csv.Append(string.Join(',', row.Select(Quote))).Append("\r\n");
        }

        return csv.ToString();

        static string Quote(string value) =>
            value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
