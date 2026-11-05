using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contoso.Corpus.Generator.Content;
using Contoso.Corpus.Generator.Writers;
using Contoso.Knowledge.Documents;

// Writes Contoso's corpus in its real formats: sources/<system>/..., the truth each file was rendered from,
// and a manifest of what each source system reports about its files. Deterministic: same input, same bytes.
var output = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine(FindSampleCodeRoot(), "corpus");
var sources = Path.Combine(output, "sources");
var truths = Path.Combine(output, "truth");
foreach (var folder in new[] { sources, truths })
{
    if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    Directory.CreateDirectory(folder);
}

var specs = Fluid.All().Concat(Motion.All()).Concat(Corporate.All()).Concat(Service.All()).Append(Catalog.PartsCatalog()).ToList();
var written = new Dictionary<string, byte[]>(StringComparer.Ordinal);
var manifest = new JsonArray();
var stopwatch = System.Diagnostics.Stopwatch.StartNew();

// Copies and emails use other files' bytes, so they go last.
foreach (var spec in specs.OrderBy(s => s.CopyOf is not null || s.Format == Format.Eml ? 1 : 0))
{
    var (bytes, truth) = Render(spec, written);
    written[spec.Path] = bytes;
    var path = Path.Combine(sources, spec.Path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllBytesAsync(path, bytes);
    File.SetLastWriteTimeUtc(path, spec.Modified.UtcDateTime);

    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
    truth = truth with
    {
        Parser = "truth",
        Source = truth.Source with { MediaType = MediaTypes.FromFileName(spec.Path), LastModified = spec.Modified, Length = bytes.Length, ContentHash = hash },
    };
    var truthFile = $"{truth.Id}@{truth.Version}.json";
    await DocumentJson.SaveAsync(truth, Path.Combine(truths, truthFile));
    manifest.Add(Entry(spec, truth, hash, bytes.Length, truthFile));
    Console.WriteLine($"{spec.Format,-5} {bytes.Length / 1024.0,8:N1} KB  {(truth.Pages.Count > 0 ? $"{truth.Pages.Count,3} pp" : "      ")}  {spec.Path}");
}

await File.WriteAllTextAsync(Path.Combine(sources, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
await File.WriteAllTextAsync(Path.Combine(sources, "intranet", "sitemap.xml"), Sitemap(specs));
Console.WriteLine($"{specs.Count} files, {written.Values.Sum(b => (long)b.Length) / 1024.0 / 1024.0:N1} MB in {stopwatch.Elapsed.TotalSeconds:N1} s -> {output}");

static (byte[] Bytes, CanonicalDocument Truth) Render(SourceSpec spec, IReadOnlyDictionary<string, byte[]> written)
{
    var truth = spec.Truth;
    var created = spec.Modified.UtcDateTime;
    if (spec.CopyOf is not null)
    {
        return (written[spec.CopyOf], truth);
    }

    switch (spec.Format)
    {
        case Format.Pdf:
            // A PDF carries a figure's caption and labels but no alternative text, so the truth doesn't either.
            var (pages, anchored) = new PageLayout(spec.Page).Run([.. truth.Blocks.Select(b => b is FigureBlock f ? f with { AltText = null } : b)]);
            var scanned = spec.ScannedAll ? pages.Select(p => p.Number).ToHashSet() : spec.ScannedPages?.Select(n => n < 0 ? pages.Count + 1 + n : n).ToHashSet() ?? [];
            // Marks can address pages from the end (-1 is the last page), like the scanned pages themselves.
            var scan = spec.Scan with { Marks = [.. spec.Scan.Marks.Select(m => m.Page < 0 ? m with { Page = pages.Count + 1 + m.Page } : m)] };
            var pdf = PdfRenderer.Render(pages, spec.Page, new PdfInfo(truth.Metadata.Title, spec.Author, spec.Modified), scanned, scan);
            // The running header and footer are part of the truth too, with their roles, so that leaks can be counted.
            var running = pages.SelectMany(p => RunningText(spec.Page, p.Number, pages.Count));
            return (pdf, truth with
            {
                Blocks = [.. anchored, .. running],
                Pages = [.. pages.Select(p => new PageInfo(p.Number, spec.Page.Width, spec.Page.Height) { HasTextLayer = !scanned.Contains(p.Number) })],
            });
        case Format.Docx:
            var docx = DocxWriter.Write(truth, spec.Docx, spec.Author, created);
            var comments = spec.Docx.Comments.Select(c => (Block)new ParagraphBlock(c.Text)
            {
                Role = BlockRoles.Comment,
                Anchor = new SourceAnchor { Path = $"comment by {c.Author}" },
            });
            var headerFooter = new[] { (spec.Docx.Header, BlockRoles.Header), (spec.Docx.Footer, BlockRoles.Footer) }
                .Where(h => h.Item1 is not null).Select(h => (Block)new ParagraphBlock(h.Item1!) { Role = h.Item2 });
            return (docx, truth with { Blocks = [.. truth.Blocks, .. comments, .. headerFooter] });
        case Format.Pptx:
            return (PptxWriter.Write(truth.Metadata.Title, spec.Author, created, spec.Slides), truth);
        case Format.Xlsx:
            var (xlsx, sheets) = XlsxWriter.Write(spec.Sheets, spec.Author, created);
            return (xlsx, truth with { Blocks = [.. truth.Blocks, .. sheets] });
        case Format.Html:
            return (Encoding.UTF8.GetBytes(HtmlWriter.Write(truth, spec.Html!)), truth);
        case Format.Eml:
            var attachments = spec.Attachments.Select(a => new MailAttachment(Path.GetFileName(a), MediaTypes.FromFileName(a), written[a])).ToList();
            var (eml, blocks) = EmlWriter.Write(spec.Thread, attachments);
            return (eml, truth with { Blocks = blocks });
        case Format.Csv:
            return (Encoding.UTF8.GetBytes(CsvWriter.Write(spec.Csv!.Value.Header, spec.Csv.Value.Rows)), truth);
        default:
            throw new NotSupportedException(spec.Format.ToString());
    }
}

static IEnumerable<Block> RunningText(PageStyle style, int page, int pages)
{
    if (style.RunningHeader is not null)
        yield return new ParagraphBlock(style.RunningHeader) { Role = BlockRoles.Header, Anchor = new SourceAnchor { Page = page } };
    if (style.RunningFooter is not null)
        yield return new ParagraphBlock(style.RunningFooter.Replace("{page}", $"{page}").Replace("{pages}", $"{pages}")) { Role = BlockRoles.Footer, Anchor = new SourceAnchor { Page = page } };
}

static JsonObject Entry(SourceSpec spec, CanonicalDocument truth, string hash, long length, string truthFile)
{
    var metadata = JsonSerializer.SerializeToNode(truth.Metadata, DocumentJson.Options)!.AsObject();
    foreach (var missing in spec.MissingMetadata)
    {
        metadata.Remove(missing);
    }

    var entry = new JsonObject
    {
        ["path"] = spec.Path,
        ["system"] = truth.Source.System,
        ["id"] = truth.Id,
        ["version"] = truth.Version,
        ["mediaType"] = MediaTypes.FromFileName(spec.Path),
        ["lastModified"] = spec.Modified.ToString("O"),
        ["length"] = length,
        ["sha256"] = hash,
        ["metadata"] = metadata,
        ["truth"] = $"truth/{truthFile}",
    };
    if (spec.Attachments.Count > 0) entry["attachments"] = new JsonArray([.. spec.Attachments.Select(a => (JsonNode)a)]);
    if (spec.CopyOf is not null) entry["copyOf"] = spec.CopyOf;
    if (spec.Note is not null) entry["note"] = spec.Note;
    return entry;
}

static string Sitemap(IEnumerable<SourceSpec> specs)
{
    var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
    foreach (var spec in specs.Where(s => s.Truth.Source.System == "intranet").OrderBy(s => s.Path, StringComparer.Ordinal))
    {
        xml.Append($"  <url><loc>https://intranet.contoso.example/{spec.Path["intranet/".Length..]}</loc><lastmod>{spec.Modified:yyyy-MM-dd}</lastmod></url>\n");
    }

    return xml.Append("</urlset>\n").ToString();
}

static string FindSampleCodeRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Contoso-Knowledge.slnx"))) return dir.FullName;
    }

    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Contoso-Knowledge.slnx"))) return dir.FullName;
    }

    throw new DirectoryNotFoundException("Run from inside the sample-code folder, or pass --out <folder>.");
}
