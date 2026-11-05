using System.Diagnostics;
using System.Text;
using Ch07.Parsing.Cli;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Parsing;
using Microsoft.ML.Tokenizers;

// Commands (from the sample-code folder):
//   evaluate [--naive] [--format pdf] [--lost]   parse <path fragment> [--naive] [--json]
//   measure    tables    timing    legacy <file>
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "evaluate";
var naive = args.Contains("--naive");
var registry = naive ? Naive() : ParserRegistry.Default;
var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

switch (command)
{
    case "parse":
        var entry = GeneratedCorpus.Register().First(e => e.Path.Contains(args[1], StringComparison.OrdinalIgnoreCase));
        var parsed = await Parse(entry, registry);
        Console.WriteLine(args.Contains("--json") ? DocumentJson.Serialize(parsed) : parsed.ToText().Text);
        break;

    case "evaluate":
        var format = args.SkipWhile(a => a != "--format").Skip(1).FirstOrDefault();
        var scores = new List<ParseScore>();
        var stopwatch = Stopwatch.StartNew();
        foreach (var e in GeneratedCorpus.Register().Where(e => e.CopyOf is null && (format is null || e.Path.EndsWith("." + format, StringComparison.OrdinalIgnoreCase))))
        {
            var truth = await GeneratedCorpus.TruthAsync(e);
            if (truth.Pages.Count > 0 && truth.Pages.All(p => !p.HasTextLayer)) continue;   // scans wait for Chapter 8
            scores.Add(ParserEvaluation.Score(e.Path, Path.GetExtension(e.Path).TrimStart('.'), truth, await Parse(e, registry)));
        }

        Console.WriteLine($"{"File",-58} {"Words",6} {"Recall",7} {"Prec.",6} {"Order",6} {"Sent.",6} {"Cells",6} {"Leaks",5}");
        foreach (var s in scores)
        {
            Console.WriteLine($"{Shorten(s.Path),-58} {s.TruthWords,6} {s.WordRecall,7:F3} {s.WordPrecision,6:F3} {s.Order,6:F3} {s.Sentences,6:F3} {(s.Tables > 0 ? $"{s.TableCells:F3}" : "-"),6} {s.Leaks,5}");
        }

        foreach (var group in scores.GroupBy(s => s.Format))
        {
            Console.WriteLine($"{group.Key,-58} {group.Sum(s => s.TruthWords),6} {group.Average(s => s.WordRecall),7:F3} {group.Average(s => s.WordPrecision),6:F3} {group.Average(s => s.Order),6:F3} " +
                $"{1 - (double)group.Sum(s => s.Lost.Count) / Math.Max(1, group.Sum(s => s.SentencesTotal)),6:F3} {(group.Any(s => s.Tables > 0) ? $"{group.Where(s => s.Tables > 0).Average(s => s.TableCells):F3}" : "-"),6} {group.Sum(s => s.Leaks),5}");
        }

        Console.WriteLine($"{scores.Count} files in {stopwatch.Elapsed.TotalSeconds:F1} s; sentences preserved: {scores.Sum(s => s.SentencesTotal - s.Lost.Count)} of {scores.Sum(s => s.SentencesTotal)}");
        if (args.Contains("--lost"))
        {
            foreach (var s in scores.Where(s => s.Lost.Count > 0))
            {
                Console.WriteLine($"-- {s.Path}");
                foreach (var l in s.Lost.Take(5)) Console.WriteLine($"   {l}");
            }
        }

        break;

    case "measure":
        // Parsing quality as retrieval quality: the same questions, retriever and chunker over each parser's text.
        var questions = RetrievalCheck.Questions();
        var before = await RetrievalCheck.RunAsync("naive", Naive(), questions, tokenizer);
        var after = await RetrievalCheck.RunAsync("layout", ParserRegistry.Default, questions, tokenizer);
        Console.WriteLine($"Golden set v1, ported: {questions.Count} dev questions with born-digital evidence; BM25 over 100-token chunks, k = 4");
        Console.WriteLine();
        Console.WriteLine($"{"Parsing",-9}{"Spans located",15}{"Recall@4",10}{"Sufficient",12}");
        foreach (var r in new[] { before, after })
        {
            Console.WriteLine($"{r.Mode,-9}{$"{r.SpansFound} of {r.Spans}",15}{r.Recall.Average(),10:F2}{r.Sufficient.Average(),12:F2}");
        }

        var recall = Bootstrap.Paired(before.Recall, after.Recall);
        var sufficient = Bootstrap.Paired(before.Sufficient, after.Sufficient);
        Console.WriteLine();
        Console.WriteLine($"Layout against naive (paired bootstrap, 10,000 resamples): Recall@4 {recall.Mean:+0.00;-0.00} [{recall.Low:+0.00;-0.00}, {recall.High:+0.00;-0.00}]; " +
            $"sufficient {sufficient.Mean:+0.00;-0.00} [{sufficient.Low:+0.00;-0.00}, {sufficient.High:+0.00;-0.00}]");
        break;

    case "tables":
        // One table, four serializations, and what each costs in tokens.
        var catalog = await Parse(GeneratedCorpus.Register().Single(e => e.Path.EndsWith("Parts Catalog 2027.xlsx", StringComparison.Ordinal)), ParserRegistry.Default);
        var table = catalog.Blocks.OfType<TableBlock>().First();
        var row = table.Rows.Skip(table.HeaderRows).First(r => r[0] == "PN-4471-C");
        var single = table with { Rows = [.. table.Rows.Take(table.HeaderRows), row] };
        Console.WriteLine($"Parts catalog, sheet Fluid Systems: {table.Rows.Count - table.HeaderRows} rows, {table.HeaderRows} header rows");
        Console.WriteLine();
        Console.WriteLine($"{"Serialization",-16}{"Whole table",13}{"One row",10}");
        foreach (var (name, whole, one) in new[]
        {
            ("Markdown", TableSerializer.Markdown(table), TableSerializer.Markdown(single)),
            ("HTML", TableSerializer.Html(table), TableSerializer.Html(single)),
            ("JSON", TableSerializer.Json(table), TableSerializer.Json(single)),
            ("Key-value rows", string.Join('\n', TableSerializer.KeyValueRows(table)), TableSerializer.KeyValueRows(single).Single()),
        })
        {
            Console.WriteLine($"{name,-16}{tokenizer.CountTokens(whole),13}{tokenizer.CountTokens(one),10}");
        }

        Console.WriteLine();
        Console.WriteLine(TableSerializer.KeyValueRows(single).Single());
        break;

    case "timing":
        // Single-threaded parse time per format: the median of five parses of each file, after one warm-up parse.
        var timings = new List<(string Format, double Ms, int Pages)>();
        foreach (var e in GeneratedCorpus.Register().Where(e => e.CopyOf is null))
        {
            var truth = await GeneratedCorpus.TruthAsync(e);
            if (truth.Pages.Count > 0 && truth.Pages.All(p => !p.HasTextLayer)) continue;
            var file = await File.ReadAllBytesAsync(GeneratedCorpus.FullPath(e));
            var context = new ParseContext(Path.GetFileName(e.Path), e.MediaType);
            await registry.ParseAsync(new MemoryStream(file), context);
            var runs = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                await registry.ParseAsync(new MemoryStream(file), context);
                runs.Add(watch.Elapsed.TotalMilliseconds);
            }

            timings.Add((Path.GetExtension(e.Path).TrimStart('.'), runs.Order().ElementAt(2), truth.Pages.Count));
        }

        Console.WriteLine($"{"Format",-8}{"Files",6}{"Median ms per file",20}{"Pages",7}{"Pages per second",18}");
        foreach (var g in timings.GroupBy(t => t.Format).OrderByDescending(g => g.Sum(t => t.Ms)))
        {
            var pages = g.Sum(t => t.Pages);
            Console.WriteLine($"{g.Key,-8}{g.Count(),6}{g.Select(t => t.Ms).Order().ElementAt(g.Count() / 2),20:F1}{(pages > 0 ? $"{pages}" : "-"),7}" +
                $"{(pages > 0 ? $"{pages / (g.Sum(t => t.Ms) / 1000):F1}" : "-"),18}");
        }

        break;

    case "legacy":
        var converter = new LegacyConverter();
        if (converter.Soffice is null)
        {
            Console.WriteLine("LibreOffice isn't installed on this machine, so legacy conversion can't run here.");
            return 1;
        }

        var bytes = await converter.ConvertAsync(await File.ReadAllBytesAsync(args[1]), Path.GetFileName(args[1]), TimeSpan.FromSeconds(60));
        Console.WriteLine($"Converted {args[1]}: {bytes.Length:N0} bytes of Open XML.");
        break;
}

return 0;

static ParserRegistry Naive() =>
    new([new PdfParser(new PdfParserOptions { Mode = PdfExtraction.Naive }), new DocxParser(new DocxParserOptions { Naive = true }),
        new HtmlParser(new HtmlParserOptions { MainContentOnly = false }), new PptxParser(), new XlsxParser(new XlsxParserOptions { Engine = SpreadsheetEngine.ExcelDataReader }),
        new CsvParser(), new EmailParser(), new MarkdownParser()]);

static async Task<CanonicalDocument> Parse(RegisterEntry entry, ParserRegistry registry)
{
    await using var stream = File.OpenRead(GeneratedCorpus.FullPath(entry));
    var content = await registry.ParseAsync(stream, new ParseContext(Path.GetFileName(entry.Path), entry.MediaType));
    var truthMetadata = (await GeneratedCorpus.TruthAsync(entry)).Metadata;
    return new CanonicalDocument(entry.Id, entry.Version, truthMetadata, new SourceReference(entry.System, entry.Path), content.Blocks) { Pages = content.Pages };
}

static string Shorten(string path) => path.Length <= 58 ? path : "…" + path[^57..];
