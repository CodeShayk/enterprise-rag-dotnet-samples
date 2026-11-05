using System.Globalization;
using ClosedXML.Excel;
using Contoso.Knowledge.Documents;
using ExcelDataReader;
using Block = Contoso.Knowledge.Documents.Block;

namespace Contoso.Knowledge.Parsing;

public enum SpreadsheetEngine
{
    /// <summary>ClosedXML: formatted values as Excel displays them, merged ranges, styles and named ranges.</summary>
    ClosedXml,

    /// <summary>ExcelDataReader: fast, forward-only raw values as cached in the file, no formatting.</summary>
    ExcelDataReader,
}

public sealed record XlsxParserOptions
{
    public SpreadsheetEngine Engine { get; init; } = SpreadsheetEngine.ClosedXml;
}

/// <summary>
/// Excel workbooks, one table per sheet. A merged title row becomes the sheet's heading; merged header cells are
/// repeated over every column they span, and the bold rows above the data are header rows. Values are the ones
/// Excel shows: a formula's cached result, formatted with the cell's number format.
/// </summary>
public sealed class XlsxParser(XlsxParserOptions? options = null) : IDocumentParser
{
    private readonly XlsxParserOptions _options = options ?? new XlsxParserOptions();

    public string Name => "xlsx";

    public string Version => _options.Engine == SpreadsheetEngine.ClosedXml ? "1.0" : "1.0-raw";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Xlsx];

    public Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_options.Engine == SpreadsheetEngine.ClosedXml ? WithClosedXml(content, cancellationToken) : WithExcelDataReader(content));

    private static ParsedContent WithClosedXml(Stream content, CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook(content);
        var blocks = new List<Block>();
        foreach (var sheet in workbook.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var used = sheet.RangeUsed();
            if (used is null) continue;
            int top = used.FirstRow().RowNumber(), bottom = used.LastRow().RowNumber();
            int left = used.FirstColumn().ColumnNumber(), right = used.LastColumn().ColumnNumber();

            // A first row merged across the whole width is the sheet's title.
            var titleRange = sheet.MergedRanges.FirstOrDefault(m => m.FirstRow().RowNumber() == top && m.FirstColumn().ColumnNumber() == left && m.LastColumn().ColumnNumber() == right);
            if (titleRange is not null)
            {
                blocks.Add(new HeadingBlock(2, sheet.Cell(top, left).GetFormattedString()) { Anchor = new SourceAnchor { Path = $"sheet {sheet.Name}!{sheet.Cell(top, left).Address}" } });
                top++;
            }

            var rows = new List<IReadOnlyList<string>>();
            var headerRows = 0;
            var inHeader = true;
            for (var r = top; r <= bottom; r++)
            {
                var cells = Enumerable.Range(left, right - left + 1).Select(c => Value(sheet, r, c)).ToList();
                if (cells.All(string.IsNullOrEmpty)) continue;
                var bold = Enumerable.Range(left, right - left + 1).Where(c => !sheet.Cell(r, c).IsEmpty() || sheet.Cell(r, c).IsMerged())
                    .All(c => (sheet.Cell(r, c).MergedRange()?.FirstCell() ?? sheet.Cell(r, c)).Style.Font.Bold);
                if (inHeader && bold) headerRows++;
                else inHeader = false;
                rows.Add(cells);
            }

            blocks.Add(new TableBlock(rows, Math.Max(1, headerRows)) { Anchor = new SourceAnchor { Path = $"sheet {sheet.Name}!{sheet.Cell(top, left).Address}:{sheet.Cell(bottom, right).Address}" } });
        }

        var properties = workbook.DefinedNames.ToDictionary(n => $"name:{n.Name}", n => n.RefersTo, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(workbook.Properties.Author)) properties["author"] = workbook.Properties.Author;
        return new ParsedContent(blocks) { Title = workbook.Properties.Title, Properties = properties };
    }

    /// <summary>A cell's displayed value; a cell covered by a merged range shows the range's value.</summary>
    private static string Value(IXLWorksheet sheet, int row, int column)
    {
        var cell = sheet.Cell(row, column);
        var source = cell.MergedRange()?.FirstCell() ?? cell;
        return TextCleanup.Normalize(source.GetFormattedString());
    }

    private static ParsedContent WithExcelDataReader(Stream content)
    {
        // ExcelDataReader needs the legacy code pages even for .xlsx. Without this line it works only in processes where
        // another library (MimeKit, for one) happened to register them first.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var reader = ExcelReaderFactory.CreateReader(content);
        var blocks = new List<Block>();
        do
        {
            var rows = new List<IReadOnlyList<string>>();
            while (reader.Read())
            {
                var cells = Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "").ToList();
                if (cells.Any(c => c.Length > 0)) rows.Add(cells);
            }

            blocks.Add(new HeadingBlock(2, reader.Name));
            blocks.Add(new TableBlock(rows, 1) { Anchor = new SourceAnchor { Path = $"sheet {reader.Name}" } });
        }
        while (reader.NextResult());

        return new ParsedContent(blocks);
    }
}

/// <summary>CSV per RFC 4180: quoted fields may hold commas, quotes and line breaks. The first row is the header.</summary>
public sealed class CsvParser : IDocumentParser
{
    public string Name => "csv";

    public string Version => "1.0";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Csv];

    public async Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return new ParsedContent([new TableBlock(Parse(text), 1)]);
    }

    public static List<IReadOnlyList<string>> Parse(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(f => f.Length > 0)) rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Any(f => f.Length > 0)) rows.Add(row);
        }

        return rows;
    }
}
