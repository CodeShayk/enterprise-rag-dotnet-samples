using System.Net;
using System.Text;
using System.Text.Json;
using Contoso.Knowledge.Documents;

namespace Contoso.Knowledge.Parsing;

/// <summary>
/// Ways to write a table for an index or a prompt. Markdown is compact and models read it well; HTML keeps
/// structure that Markdown can't (merged cells, several header rows); JSON names every value; key-value rows
/// repeat the headers in every row, so that a row cut out of the table still says what each value means.
/// </summary>
public static class TableSerializer
{
    /// <summary>Each column's header path, such as <c>Price (USD) › Net price</c>, built from all header rows.</summary>
    public static IReadOnlyList<string> HeaderPaths(TableBlock table)
    {
        var headerRows = table.Rows.Take(Math.Max(table.HeaderRows, 1)).ToList();
        var columns = table.Rows.Max(r => r.Count);
        return [.. Enumerable.Range(0, columns).Select(c =>
            string.Join(" › ", headerRows.Select(r => c < r.Count ? r[c] : "").Where(h => h.Length > 0).Distinct()))];
    }

    public static IEnumerable<IReadOnlyList<string>> DataRows(TableBlock table) => table.Rows.Skip(Math.Max(table.HeaderRows, 1));

    private static readonly JsonSerializerOptions Compact = new(DocumentJson.Options) { WriteIndented = false };

    public static string Markdown(TableBlock table) => table.ToMarkdown();

    public static string Html(TableBlock table)
    {
        var html = new StringBuilder("<table>");
        foreach (var (row, i) in table.Rows.Select((r, i) => (r, i)))
        {
            var tag = i < table.HeaderRows ? "th" : "td";
            html.Append("<tr>").Append(string.Concat(row.Select(c => $"<{tag}>{WebUtility.HtmlEncode(c)}</{tag}>"))).Append("</tr>");
        }

        return html.Append("</table>").ToString();
    }

    public static string Json(TableBlock table)
    {
        var headers = HeaderPaths(table);
        var rows = DataRows(table).Select(r => headers.Select((h, c) => (h, v: c < r.Count ? r[c] : "")).ToDictionary(p => p.h, p => p.v));
        return JsonSerializer.Serialize(rows, Compact);
    }

    /// <summary>One line per data row: <c>Part number: PN-4471-C; Description: …; Price (USD) › Net price: 1,334.00</c>.</summary>
    public static IEnumerable<string> KeyValueRows(TableBlock table)
    {
        var headers = HeaderPaths(table);
        return DataRows(table).Select(r => string.Join("; ", headers.Select((h, c) => $"{h}: {(c < r.Count ? r[c] : "")}")));
    }
}
