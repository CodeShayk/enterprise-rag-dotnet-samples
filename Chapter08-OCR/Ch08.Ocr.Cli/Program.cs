using System.Diagnostics;
using System.Text;
using Ch08.Ocr.Cli;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Ocr;
using Contoso.Knowledge.Parsing;

// Commands (from the sample-code folder):
//   read <file fragment> [--engine <name>] [--prep <name>]     the body text an engine reads, and the marks it set aside
//   engines [--engines a,b] [--prep <name>] [--correct]         error rates, confidence and speed per scanned document
//   triage [--all]    parse <file fragment> [--engine <name>] [--json]    layout [--engines a,b] [--lost]    qc [--engines a,b]
// Engines: tesseract-best, tesseract-fast, paddle, paddle-full.
// Preparations, each adding a step: none, resample (300 DPI), deskew, despeckle, default (drops colored ink), binarize.
Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", "1");   // one core per engine, so timings are per core
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "engines";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var prep = Reading.Prep(Option("--prep") ?? "default");

switch (command)
{
    case "read":
    {
        using var engine = Reading.Engine(Option("--engine") ?? "tesseract-best");
        foreach (var document in await Scans.AllAsync(args[1]))
        {
            var result = Reading.Read(document, engine, prep);
            Console.WriteLine($"== {document.Name}: CER {result.Cer:P1}, WER {result.Wer:P1}, confidence {result.Confidence:F2}");
            Console.WriteLine(result.BodyText);
            foreach (var mark in result.Annotations) Console.WriteLine($"   [mark] {mark.Text} ({mark.Confidence:F2})");
        }

        break;
    }

    case "engines":
    {
        var documents = await Scans.AllAsync();
        Console.WriteLine($"{"Engine",-16} {"Document",-46} {"Pages",5} {"CER",7} {"WER",7} {"Conf.",6} {"s/page",7}");
        foreach (var name in (Option("--engines") ?? "tesseract-fast,tesseract-best,paddle,paddle-full").Split(','))
        {
            using var engine = Reading.Engine(name);
            var results = documents.Select(d => (Document: d, Result: Reading.Read(d, engine, prep, args.Contains("--correct")))).ToList();
            foreach (var (d, r) in results)
            {
                Console.WriteLine($"{name,-16} {d.Name,-46} {d.Pages.Count,5} {r.Cer,7:P1} {r.Wer,7:P1} {r.Confidence,6:F2} {r.OcrSeconds / d.Pages.Count,7:F2}");
            }

            var chars = results.Sum(r => r.Result.ReferenceChars);
            var pages = results.Sum(r => r.Document.Pages.Count);
            var seconds = results.Sum(r => r.Result.OcrSeconds);
            var prepSeconds = results.Sum(r => r.Result.PrepSeconds);
            Console.WriteLine($"{name,-16} {"all scanned pages",-46} {pages,5} {results.Sum(r => r.Result.Cer * r.Result.ReferenceChars) / chars,7:P1} " +
                $"{results.Sum(r => r.Result.Wer * r.Result.ReferenceWords) / results.Sum(r => r.Result.ReferenceWords),7:P1} " +
                $"{results.Average(r => r.Result.Confidence),6:F2} {seconds / pages,7:F2}   {60 * pages / seconds:F1} pages per core-minute, " +
                $"rendering and preparation {prepSeconds / pages:F2} s/page" +
                (args.Contains("--correct") ? $", {results.Sum(r => r.Result.Corrections)} corrections" : ""));
        }

        break;
    }

    case "measure":
    {
        // OCR quality as retrieval quality: the same questions, retriever and chunker over the corpus without OCR,
        // then with each engine reading the scans.
        var questions = RetrievalCheck.Questions();
        var tokenizer = Microsoft.ML.Tokenizers.TiktokenTokenizer.CreateForEncoding("o200k_base");
        var others = ParserRegistry.Default.Parsers.Where(p => p.Name != "pdf").ToList();
        var results = new List<RetrievalCheck.ModeResult> { await RetrievalCheck.RunAsync("no OCR", ParserRegistry.Default, questions, tokenizer, ocr: false) };
        foreach (var name in (Option("--engines") ?? "tesseract-best,paddle").Split(','))
        {
            using var engine = Reading.Engine(name);
            foreach (var correct in new[] { false, true })
            {
                var registry = new ParserRegistry(others.Append(new ScannedPdfParser(engine, prep, postCorrect: correct)));
                results.Add(await RetrievalCheck.RunAsync(correct ? name : $"{name}, raw", registry, questions, tokenizer, ocr: true));
            }
        }

        Console.WriteLine($"Golden set v1, ported: {questions.Count} dev questions with evidence on scanned pages; BM25 over 100-token chunks, k = 4");
        Console.WriteLine();
        Console.WriteLine($"{"Corpus",-22}{"Spans located",15}{"Recall@4",10}{"Sufficient",12}");
        foreach (var r in results) Console.WriteLine($"{r.Mode,-22}{$"{r.SpansFound} of {r.Spans}",15}{r.Recall.Average(),10:F2}{r.Sufficient.Average(),12:F2}");
        var best = results.Last();
        var recall = Contoso.Knowledge.Evaluation.Bootstrap.Paired(results[0].Recall, best.Recall);
        var sufficient = Contoso.Knowledge.Evaluation.Bootstrap.Paired(results[0].Sufficient, best.Sufficient);
        Console.WriteLine();
        Console.WriteLine($"{best.Mode} against no OCR (paired bootstrap, 10,000 resamples): Recall@4 {recall.Mean:+0.00;-0.00} [{recall.Low:+0.00;-0.00}, {recall.High:+0.00;-0.00}]; " +
            $"sufficient {sufficient.Mean:+0.00;-0.00} [{sufficient.Low:+0.00;-0.00}, {sufficient.High:+0.00;-0.00}]");
        break;
    }

    case "qc":
    {
        // Does the engine's confidence point at its errors? Body words aligned to the truth; a word below the
        // threshold is flagged for review.
        var documents = await Scans.AllAsync();
        double[] thresholds = [0.5, 0.7, 0.8, 0.9, 0.95];
        Console.WriteLine($"{"Engine",-16} {"Words",6} {"Wrong",6}  " + string.Join("  ", thresholds.Select(t => $"below {t:F2}: flagged, caught")));
        foreach (var name in (Option("--engines") ?? "tesseract-best,paddle").Split(','))
        {
            using var engine = Reading.Engine(name);
            var scored = new List<(double Confidence, bool Right)>();
            foreach (var document in documents)
            {
                var result = Reading.Read(document, engine, prep);
                var hypothesis = result.Words.Select(w => w.Text).ToList();
                var matched = OcrQuality.MatchedWords(OcrQuality.Words(Scans.BodyText(document)), hypothesis);
                scored.AddRange(result.Words.Select((w, i) => (w.Confidence, matched[i])));
            }

            var wrong = scored.Count(s => !s.Right);
            var cells = thresholds.Select(t =>
            {
                var flagged = scored.Where(s => s.Confidence < t).ToList();
                return $"{(double)flagged.Count / scored.Count,14:P1}, {(double)flagged.Count(s => !s.Right) / Math.Max(1, wrong),6:P0}";
            });
            Console.WriteLine($"{name,-16} {scored.Count,6} {wrong,6}  " + string.Join("  ", cells));
        }

        break;
    }

    case "triage":
    {
        // Which pages need OCR, and how good each scan is. --all lists the pages with a text layer too.
        var counts = new Dictionary<PageKind, int>();
        Console.WriteLine($"{"File",-36} {"Page",4} {"Kind",-8} {"Chars",5} {"Image",5} {"DPI",4} {"Skew",6} {"Contr.",6} {"Specks",6} {"Sharp.",6} {"Grade",-5}");
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && e.MediaType == MediaTypes.Pdf))
        {
            var pdf = await File.ReadAllBytesAsync(GeneratedCorpus.FullPath(entry));
            foreach (var t in Triage.Assess(pdf))
            {
                counts[t.Kind] = counts.GetValueOrDefault(t.Kind) + 1;
                if (t.Kind == PageKind.TextLayer && !args.Contains("--all")) continue;
                var name = Path.GetFileNameWithoutExtension(entry.Path);
                var line = $"{(name.Length > 36 ? name[..35] + "…" : name),-36} {t.Page,4} {t.Kind,-8} {t.Characters,5} {t.ImageCoverage,5:F2} {t.NativeDpi,4}";
                if (t.Kind == PageKind.Scanned)
                {
                    using var image = PageImages.Render(pdf, t.Page, t.NativeDpi ?? 300);
                    var q = ScanQuality.Measure(image, t.NativeDpi ?? 300);
                    line += $" {q.SkewDegrees,5:F2}° {q.Contrast,6:F2} {q.SpecksPerMegapixel,6:F0} {q.Sharpness,6:F0} {q.Grade,-5}";
                }

                Console.WriteLine(line);
            }
        }

        Console.WriteLine($"Pages: {string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Value} {c.Key}"))}");
        break;
    }

    case "parse":
    {
        using var engine = Reading.Engine(Option("--engine") ?? "tesseract-best");
        var parser = new ScannedPdfParser(engine, prep);
        foreach (var document in await Scans.AllAsync(args[1]))
        {
            var parsed = await parser.ParseAsync(new MemoryStream(document.Pdf), new ParseContext(Path.GetFileName(document.Entry.Path), MediaTypes.Pdf));
            var canonical = new CanonicalDocument(document.Entry.Id, document.Entry.Version, document.Truth.Metadata, document.Truth.Source, parsed.Blocks);
            Console.WriteLine(args.Contains("--json") ? DocumentJson.Serialize(canonical) : canonical.ToText().Text);
            foreach (var (key, value) in parsed.Properties.Where(p => p.Key.StartsWith("ocr", StringComparison.Ordinal) || p.Key == "references")) Console.WriteLine($"{key}: {value}");
            foreach (var item in parser.LastReview) Console.WriteLine($"review p{item.Page} {item.Reason}: \"{item.Text}\" ({item.Confidence:F2})");
        }

        break;
    }

    case "layout":
    {
        Console.WriteLine($"{"Engine",-16} {"Document",-44} {"Words",6} {"Recall",7} {"Prec.",6} {"Order",6} {"Sent.",6} {"Cells",6} {"Review",6}");
        foreach (var name in (Option("--engines") ?? "tesseract-best,paddle").Split(','))
        {
            using var engine = Reading.Engine(name);
            var parser = new ScannedPdfParser(engine, prep);
            foreach (var document in await Scans.AllAsync())
            {
                var parsed = await parser.ParseAsync(new MemoryStream(document.Pdf), new ParseContext(Path.GetFileName(document.Entry.Path), MediaTypes.Pdf));
                var canonical = new CanonicalDocument(document.Entry.Id, document.Entry.Version, document.Truth.Metadata, document.Truth.Source, parsed.Blocks);
                var s = ParserEvaluation.Score(document.Entry.Path, "pdf", document.Truth, canonical);
                Console.WriteLine($"{name,-16} {document.Name,-44} {s.TruthWords,6} {s.WordRecall,7:F3} {s.WordPrecision,6:F3} {s.Order,6:F3} {s.Sentences,6:F3} " +
                    $"{(s.Tables > 0 ? $"{s.TableCells:F3}" : "-"),6} {parser.LastReview.Count,6}");
                if (args.Contains("--lost")) foreach (var l in s.Lost) Console.WriteLine($"   lost: {l}");
            }
        }

        break;
    }

    case "lines":
    {
        using var engine = Reading.Engine(Option("--engine") ?? "paddle");
        var document = (await Scans.AllAsync(args[1])).Single();
        var number = int.Parse(Option("--page") ?? "1");
        var dpi = PageImages.NativeDpi(document.Pdf, number) ?? 300;
        using var page = PageImages.Render(document.Pdf, number, dpi);
        using var prepared = ImagePrep.Prepare(page, dpi, prep);
        if (Option("--save") is { } save) OpenCvSharp.Cv2.ImWrite(save, prepared.Image);
        foreach (var grid in ScanTables.Find(prepared.Image))
        {
            Console.WriteLine($"grid {grid.Box.Left:F0},{grid.Box.Top:F0},{grid.Box.Right:F0},{grid.Box.Bottom:F0}: rows {string.Join(' ', grid.Rows.Select(r => r.ToString("F0")))}; columns {string.Join(' ', grid.Columns.Select(c => c.ToString("F0")))}");
        }

        foreach (var line in engine.Recognize(prepared.Image).Lines)
        {
            Console.WriteLine($"{line.Box.Top,5:F0} {line.Box.Left,5:F0} {prepared.ColoredShare(line.Box),5:F2} {line.Confidence,5:F2} {line.Text}");
        }

        break;
    }

    default:
        Console.WriteLine("Commands: read, engines, triage, parse, layout, qc, lines");
        break;
}

/// <summary>Reads a scanned document page by page and scores its body text against the truth.</summary>
internal static class Reading
{
    public sealed record Result(
        string BodyText, IReadOnlyList<OcrLine> Annotations, double Cer, double Wer, double Confidence,
        int ReferenceChars, int ReferenceWords, double PrepSeconds, double OcrSeconds, IReadOnlyList<double> Skews,
        IReadOnlyList<OcrWord> Words, int Corrections);

    /// <param name="correct">Apply <see cref="OcrText"/>'s post-correction rules to each line.</param>
    public static Result Read(Scans.ScannedDocument document, IOcrEngine engine, PrepOptions prep, bool correct = false)
    {
        var body = new List<string>();
        var words = new List<OcrWord>();
        var corrections = 0;
        var annotations = new List<OcrLine>();
        var confidences = new List<(double Confidence, int Chars)>();
        var skews = new List<double>();
        double prepSeconds = 0, ocrSeconds = 0;
        foreach (var number in document.Pages)
        {
            var dpi = PageImages.NativeDpi(document.Pdf, number) ?? 300;
            var stopwatch = Stopwatch.StartNew();
            using var page = PageImages.Render(document.Pdf, number, dpi);
            using var prepared = ImagePrep.Prepare(page, dpi, prep);
            prepSeconds += stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
            var ocr = engine.Recognize(prepared.Image);
            ocrSeconds += stopwatch.Elapsed.TotalSeconds;
            skews.Add(prepared.SkewDegrees);

            // Body lines only: running headers and footers sit in the top and bottom 8.5% of the page (Chapter 7's
            // rule), and stamps and pen marks are colored ink.
            var height = prepared.Image.Height;
            var lines = ocr.Lines.Where(l => l.Box.Bottom > 0.085 * height && l.Box.Top < 0.915 * height).ToList();
            var marks = lines.Where(l => prepared.ColoredShare(l.Box) >= 0.3).ToList();
            annotations.AddRange(marks.Concat(Marks.Read(prepared, engine)));
            var printed = lines.Except(marks).ToList();
            var rows = OcrQuality.RowMajor(printed);
            body.Add(string.Join('\n', rows.Select(r => string.Join(' ', r.Select(l =>
            {
                if (!correct) return l.Text;
                var fixedText = OcrText.Correct(l.Text, out var n);
                corrections += n;
                return fixedText;
            })))));
            words.AddRange(rows.SelectMany(r => r).SelectMany(l => l.Words).Where(w => !OcrQuality.IsMarker(w.Text)));
            confidences.AddRange(printed.Select(l => (l.Confidence, l.Text.Length)));
        }

        var text = string.Join('\n', body);
        var reference = Scans.BodyText(document);
        var chars = confidences.Sum(c => c.Chars);
        return new Result(text, annotations, OcrQuality.Cer(reference, text), OcrQuality.Wer(reference, text),
            chars == 0 ? 0 : confidences.Sum(c => c.Confidence * c.Chars) / chars,
            OcrQuality.Normalize(reference).Length, OcrQuality.Words(reference).Length, prepSeconds, ocrSeconds, skews, words, corrections);
    }

    public static IOcrEngine Engine(string name) => name switch
    {
        "tesseract-best" => new TesseractEngine("best"),
        "tesseract-fast" => new TesseractEngine("fast"),
        "paddle" => new PaddleEngine(),
        "paddle-full" => new PaddleEngine(maxSide: 4000),
        _ => throw new ArgumentException($"Unknown engine {name}"),
    };

    /// <summary>A ladder: each preparation adds one step to the one before.</summary>
    public static PrepOptions Prep(string name) => name switch
    {
        "none" => PrepOptions.None,
        "resample" => PrepOptions.None with { TargetDpi = 300 },
        "deskew" => PrepOptions.None with { TargetDpi = 300, Deskew = true },
        "despeckle" => PrepOptions.None with { TargetDpi = 300, Deskew = true, Despeckle = true },
        "default" => PrepOptions.Default,
        "binarize" => PrepOptions.Default with { Binarize = true },
        _ => throw new ArgumentException($"Unknown preparation {name}"),
    };
}
