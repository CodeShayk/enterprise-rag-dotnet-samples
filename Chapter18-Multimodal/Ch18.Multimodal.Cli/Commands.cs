using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Parsing;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;
using Contoso.Knowledge.Vision;
using Microsoft.Extensions.AI;
using SkiaSharp;

namespace Ch18.Multimodal.Cli;

public static partial class Commands
{
    /// <summary>The modality of a golden question's evidence: a figure, a table or running text, from the blocks its spans are in.</summary>
    public static string Modality(Lab lab, GoldenQuestion question)
    {
        var blocks = lab.Spans(question).Select(lab.BlockOf).OfType<Block>().ToList();
        return blocks.Any(b => b is FigureBlock) ? "figure" : blocks.Any(b => b is TableBlock) ? "table" : "text";
    }

    public static bool OnScan(Lab lab, GoldenQuestion question) =>
        lab.Spans(question).Any(s => lab.Pdf(s.Doc) is { } pdf && lab.BlockOf(s)?.Anchor?.Page is int page && pdf.Scanned(page));

    /// <summary>Where Contoso's evidence lives: PDF pages and scans, block kinds, and the golden set's questions by the modality of their evidence.</summary>
    public static void Inventory(Lab lab)
    {
        var pages = lab.Pdfs.Sum(p => p.Pages.Count);
        var scanned = lab.Pdfs.Sum(p => p.Pages.Count(t => t.Kind == Contoso.Knowledge.Ocr.PageKind.Scanned));
        Console.WriteLine($"PDFs: {lab.Pdfs.Count} documents, {pages} pages, {scanned} scanned ({string.Join(", ", lab.Pdfs.Where(p => p.Pages.Any(t => t.Kind == Contoso.Knowledge.Ocr.PageKind.Scanned)).Select(p => p.Id))})");
        var blocks = lab.Sources.Values.SelectMany(s => s.Document.Blocks).ToList();
        Console.WriteLine($"Blocks in {lab.Sources.Count} documents: {blocks.Count(b => b is TableBlock)} tables ({blocks.OfType<TableBlock>().Sum(t => t.Rows.Count - t.HeaderRows)} rows), {blocks.Count(b => b is FigureBlock)} figures, {blocks.Count(b => b is ParagraphBlock or ListItemBlock)} paragraphs and list items");
        Console.WriteLine();
        Console.WriteLine($"{"Golden set v2, answerable",-28} {"dev",5} {"test",5}");
        var answerable = lab.Questions.Where(q => q.Answerable).ToList();
        foreach (var (name, test) in new (string, Func<GoldenQuestion, bool>)[]
        {
            ("evidence in a table row", q => Modality(lab, q) == "table"),
            ("evidence in a figure", q => Modality(lab, q) == "figure"),
            ("evidence in running text", q => Modality(lab, q) == "text"),
            ("evidence on a scanned page", q => OnScan(lab, q)),
            ("evidence all in PDFs", q => PageIndex.Targets(lab, q).Count > 0),
        })
        {
            Console.WriteLine($"{name,-28} {answerable.Count(q => q.Split == "dev" && test(q)),5} {answerable.Count(q => q.Split == "test" && test(q)),5}");
        }
    }

    /// <summary>
    /// Page-level search with each representation: the page's text with BM25, its image with CLIP, and both fused;
    /// on golden questions whose evidence is in PDFs and on the figure tasks. A hit is a page that holds labeled evidence.
    /// </summary>
    public static async Task PagesAsync(Lab lab)
    {
        using var clip = Lab.Clip();
        var pages = await PageIndex.LoadAsync(lab, clip);
        var bm25 = PageIndex.Bm25(pages);
        var sets = new List<(string Name, List<(string Question, Person Asker, HashSet<(string, int)> Targets)> Items)>();
        foreach (var split in new[] { "dev", "test" })
        {
            var items = lab.Questions.Where(q => q.Answerable && q.Split == split).Select(q => (q, t: PageIndex.Targets(lab, q))).Where(x => x.t.Count > 0)
                .Select(x => (x.q.Question, People.Find(x.q.Asker)!, x.t.ToHashSet())).ToList();
            sets.Add(($"golden {split}, evidence in PDFs", items));
            sets.Add(($"golden {split}, evidence on scans", [.. items.Where(i => i.Item3.Any(t => pages.Any(p => p.Doc == t.Item1 && p.Page == t.Item2 && p.Scanned)))]));
        }

        sets.Add(("figure tasks", [.. PageIndex.FigureTasks.Select(f => (f.Question, People.Find(f.Asker)!, new HashSet<(string, int)> { (f.Doc, f.Page) }))]));
        Console.WriteLine($"{pages.Count} pages ({pages.Count(p => p.Scanned)} scanned)\n");
        Console.WriteLine($"{"Questions",-32} {"n",3} {"text @1",8} {"@3",5} {"image @1",9} {"@3",5} {"fused @1",9} {"@3",5}");
        foreach (var (name, items) in sets)
        {
            var row = new StringBuilder($"{name,-32} {items.Count,3}");
            foreach (var method in new[] { "text", "image", "fused" })
            {
                var ranks = items.Select(i => PageIndex.Search(method, i.Question, i.Asker, pages, bm25, clip).Select(p => (pages[p].Doc, pages[p].Page)).ToList()).ToList();
                double At(int k) => items.Select((i, n) => ranks[n].Take(k).Any(i.Targets.Contains)).Count(x => x) / (double)Math.Max(1, items.Count);
                row.Append($" {At(1),8:F2} {At(3),5:F2}".Replace(" ", method == "text" ? " " : " "));
            }

            Console.WriteLine(row);
        }

        Console.WriteLine();
        var scans = pages.Where(p => p.Scanned).ToList();
        var digital = pages.Where(p => !p.Scanned).ToList();
        var clock = Stopwatch.StartNew();
        foreach (var f in PageIndex.FigureTasks) clip.EmbedText(f.Question);
        Console.WriteLine($"Cost a page on the CPU: OCR of a scan {Median(scans.Select(p => p.TextMs)) / 1000:F1} s, text layer {Median(digital.Select(p => p.TextMs)):F0} ms, render at 96 DPI {Median(pages.Select(p => p.RenderMs)):F0} ms, CLIP image {Median(pages.Select(p => p.ClipMs)):F0} ms; CLIP text {clock.Elapsed.TotalMilliseconds / PageIndex.FigureTasks.Count:F0} ms a question");
    }

    /// <summary>
    /// Answers over tables, from Chapter 14's recorded answers: every quantity a claim states checked against the block it
    /// cites (number and unit), and every quote located on its PDF page, which is what a visual citation needs.
    /// </summary>
    public static async Task TablesAsync(Lab lab, string split)
    {
        using var clip = Lab.Clip();
        var pages = await PageIndex.LoadAsync(lab, clip);
        var chat = Lab.AnswerChat();
        var generator = await lab.GeneratorAsync(chat);
        var questions = lab.Questions.Where(q => q.Answerable && q.Split == split && Modality(lab, q) == "table").ToList();
        var (claims, withQuantities, wrongNumber, wrongUnit, verifiedButWrong, respelled, onPdf, located, onScan, locatedOnScan) = (0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var examples = new List<string>();
        foreach (var q in questions)
        {
            var answer = await generator.AnswerAsync(q.Question, People.Find(q.Asker)!);
            foreach (var claim in answer.Claims.Where(c => c.SourceExists))
            {
                claims++;
                var block = answer.Evidence.First(b => b.Id == claim.Source);
                var problems = NumericCheck.Check(claim.Text, block.Text, q.Question);
                if (NumericCheck.Quantities(claim.Text).Count > 0) withQuantities++;
                if (problems.Any(p => p.Problem == "not in the source")) wrongNumber++;
                if (problems.Any(p => p.Problem != "not in the source")) wrongUnit++;
                if (problems.Count > 0 && claim.QuoteFound) verifiedButWrong++;
                var stated = NumericCheck.Quantities(block.Text);
                respelled += NumericCheck.Quantities(claim.Text).Count(x => x.Unit.Length > 0 && stated.Any(s => s.Value == x.Value && s.Unit == x.Unit)
                    && !stated.Any(s => s.Value == x.Value && Squeezed(s.Text) == Squeezed(x.Text)));
                foreach (var p in problems.Take(1)) examples.Add($"{q.Id}: \"{claim.Text}\" [{block.DocumentId}] {p.Claimed.Text}: {p.Problem}");
                if (problems.Count > 0 && Environment.GetEnvironmentVariable("CH18_DEBUG") == "1") examples.Add($"    source: {block.Text.ReplaceLineEndings(" | ")[..Math.Min(400, block.Text.Length)]}");

                // Where the quote is on the page: the cited block's page from its anchor, the quote among that page's words.
                if (!claim.QuoteFound || lab.Pdf(block.DocumentId) is not { } pdf || lab.Sources[block.DocumentId].Projection.BlockAt(block.Segments[0].Start) is not { } at
                    || lab.Sources[block.DocumentId].Document.Blocks[at.Index].Anchor?.Page is not int page) continue;
                var scanned = pages.First(u => u.Doc == pdf.Id && u.Page == page).Scanned;
                onPdf++;
                onScan += scanned ? 1 : 0;
                if (PageIndex.Locate(pages, pdf.Id, page, claim.Quote) is null)
                {
                    if (Environment.GetEnvironmentVariable("CH18_DEBUG") == "1") examples.Add($"    not located on {pdf.Id} p{page}: \"{claim.Quote}\"");
                    continue;
                }

                located++;
                locatedOnScan += scanned ? 1 : 0;
            }
        }

        Console.WriteLine($"Golden set v2 {split}: {questions.Count} questions with evidence in table rows; {chat.Replayed} answers replayed, {chat.Recorded} recorded");
        Console.WriteLine($"Claims citing a source: {claims}; stating a quantity: {withQuantities}");
        Console.WriteLine($"  a number the cited source doesn't state: {wrongNumber}; the source's number in another unit: {wrongUnit}; flagged with a verified quote: {verifiedButWrong}");
        Console.WriteLine($"  quantities that match only once units are normalized (such as 2,000 operating hours against 2,000 h): {respelled}");
        Console.WriteLine($"Verified quotes on a PDF page: {onPdf} ({onScan} on scans); located on the page: {located} ({locatedOnScan} on scans)");
        foreach (var e in examples.Take(Environment.GetEnvironmentVariable("CH18_DEBUG") == "1" ? 60 : 6)) Console.WriteLine($"  {e}");
    }

    /// <summary>Whether the hits' returned spans hold every fact a golden question's answer needs.</summary>
    public static bool Holds(Lab lab, GoldenQuestion question, IEnumerable<Hit> hits) =>
        lab.Spans(question).GroupBy(s => s.Fact).All(f => f.Any(s => hits.Any(h => h.Chunk.Document.Id == s.Doc && h.Chunk.Chunk.ReturnStart < s.End && s.Start < h.Chunk.Chunk.ReturnEnd)));

    /// <summary>
    /// Table retrieval with three indexes over the same parents: the pipeline's children, where a table that fits its
    /// parent is cut into runs of Markdown lines and only the first run has the header row; the same children with the
    /// table's caption and header row in every run's index header; and those plus a summary child per table. Only what's
    /// indexed changes, never what's returned. Measured with the hybrid re-ranked retriever at four.
    /// </summary>
    public static async Task TableChunksAsync(Lab lab)
    {
        var headed = Headed(lab);
        var configurations = new (string Name, IReadOnlyList<IndexedChunk> Chunks)[]
        {
            ("Markdown runs (as indexed)", lab.Chunks),
            ("+ header row in every run", headed),
            ("+ a summary per table", [.. headed, .. Summaries(lab, headed.Count)]),
        };
        Console.WriteLine("Questions with evidence in a table row; evidence holds every fact at 4\n");
        Console.WriteLine($"{"Index",-28} {"chunks",7} {"dev",7} {"test",7} {"build s",8}");
        foreach (var (name, chunks) in configurations)
        {
            var clock = Stopwatch.StartNew();
            var retriever = await lab.RetrieverAsync(chunks);
            var seconds = clock.Elapsed.TotalSeconds;
            var cells = new List<string>();
            foreach (var split in new[] { "dev", "test" })
            {
                var questions = lab.Questions.Where(q => q.Answerable && q.Split == split && Modality(lab, q) == "table").ToList();
                var held = 0;
                foreach (var q in questions) held += Holds(lab, q, (await retriever.RetrieveAsync(new RetrievalRequest(q.Question, People.Find(q.Asker)!, 4))).Hits) ? 1 : 0;
                cells.Add($"{held}/{questions.Count}");
            }

            Console.WriteLine($"{name,-28} {chunks.Count,7} {cells[0],7} {cells[1],7} {seconds,8:F1}");
        }
    }

    /// <summary>Header propagation: every child cut from a table's Markdown gets the table's caption and header row in its index header.</summary>
    public static List<IndexedChunk> Headed(Lab lab) =>
        [.. lab.Chunks.Select(c =>
        {
            var source = lab.Sources[c.Document.Id];
            var tables = Tables(source).Where(t => t.Span.Start < c.Chunk.End && c.Chunk.Start < t.Span.End).ToList();
            if (tables.Count == 0 || c.Chunk.Kind == "table-rows") return c;
            var heads = tables.Select(t => $"{t.Table.Caption}\n| {string.Join(" | ", TableSerializer.HeaderPaths(t.Table))} |".TrimStart());
            return c with { Chunk = c.Chunk with { Header = string.Join('\n', heads.Prepend(c.Chunk.Header ?? "")).TrimStart() } };
        })];

    /// <summary>
    /// A summary child per table, returning the parent its rows return: the caption, the column names and the first
    /// column's values, so a question about the table as a whole has something to match.
    /// </summary>
    public static IEnumerable<IndexedChunk> Summaries(Lab lab, int first)
    {
        var ordinal = first;
        foreach (var (id, source) in lab.Sources)
        {
            foreach (var (span, table) in Tables(source))
            {
                if (lab.Chunks.FirstOrDefault(c => c.Document.Id == id && c.Chunk.Start < span.End && span.Start < c.Chunk.End) is not { } owner) continue;
                var text = $"{table.Caption ?? "Table"}. Columns: {string.Join(", ", TableSerializer.HeaderPaths(table))}. Rows: {string.Join(", ", TableSerializer.DataRows(table).Select(r => r.FirstOrDefault()))}.";
                yield return owner with { Ordinal = ordinal++, Chunk = new Chunk(id, span.Start, span.End, text) { Header = owner.Chunk.Header, Kind = "table-summary", Returns = (owner.Chunk.ReturnStart, owner.Chunk.ReturnEnd) } };
            }
        }
    }

    private static IEnumerable<(BlockSpan Span, TableBlock Table)> Tables(ChunkSource source) =>
        source.Projection.Blocks.Where(b => source.Document.Blocks[b.Index] is TableBlock).Select(b => (b, (TableBlock)source.Document.Blocks[b.Index]));

    /// <summary>
    /// Modality routing: the evidence's modality from the labels, guessed from the question by rules and by bge-small's
    /// nearest written example; then, for table questions, a hard filter to chunks that hold a table and a soft
    /// preference for them, against the ranking as it is.
    /// </summary>
    public static async Task RouteAsync(Lab lab)
    {
        var bge = new OnnxEmbeddingModel(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);
        var router = await SemanticRouter.CreateAsync(new OnnxEmbeddingGenerator(bge, queries: true), RouterExamples, "text");
        var items = lab.Questions.Where(q => q.Answerable).Select(q => (q.Question, Label: Modality(lab, q), q.Split))
            .Concat(PageIndex.FigureTasks.Select(f => (f.Question, Label: "figure", Split: "figure tasks"))).ToList();
        Console.WriteLine($"{"Router",-22} {"table",12} {"figure",12} {"text",12} {"all",10}");
        foreach (var (name, route) in new (string, Func<string, Task<string>>)[] { ("rules", q => Task.FromResult(Rules(q))), ("nearest example", q => router.RouteAsync(q)) })
        {
            var guesses = new List<(string Label, string Guess)>();
            foreach (var item in items) guesses.Add((item.Label, await route(item.Question)));
            string Recall(string label) { var g = guesses.Where(x => x.Label == label).ToList(); return $"{g.Count(x => x.Guess == label)}/{g.Count}"; }
            Console.WriteLine($"{name,-22} {Recall("table"),12} {Recall("figure"),12} {Recall("text"),12} {guesses.Count(x => x.Label == x.Guess) + "/" + guesses.Count,10}");
        }

        // One index with modality metadata: does filtering a table question's candidates to table blocks keep its evidence?
        var retriever = await lab.RetrieverAsync();
        Console.WriteLine();
        foreach (var split in new[] { "dev", "test" })
        {
            var (n, plain, starts, filtered, first) = (0, 0, 0, 0, 0);
            foreach (var q in lab.Questions.Where(q => q.Answerable && q.Split == split))
            {
                var label = Modality(lab, q);
                if (label != "table") continue;
                n++;
                var asker = People.Find(q.Asker)!;
                var hits = (await retriever.RetrieveAsync(new RetrievalRequest(q.Question, asker, 8))).Hits;
                bool StartsInTable(Hit h) => lab.Sources[h.Chunk.Document.Id] is var src && src.Projection.BlockAt(h.Chunk.Chunk.Start) is { } at && src.Document.Blocks[at.Index] is TableBlock;
                bool InTable(Hit h) => Tables(lab.Sources[h.Chunk.Document.Id]).Any(t => t.Span.Start < h.Chunk.Chunk.End && h.Chunk.Chunk.Start < t.Span.End);
                plain += Holds(lab, q, hits.Take(4)) ? 1 : 0;
                starts += Holds(lab, q, hits.Where(StartsInTable).Take(4)) ? 1 : 0;
                filtered += Holds(lab, q, hits.Where(InTable).Take(4)) ? 1 : 0;
                first += Holds(lab, q, hits.Where(InTable).Concat(hits.Where(h => !InTable(h))).Take(4)) ? 1 : 0;
            }

            Console.WriteLine($"Table questions, {split}: evidence holds every fact {plain}/{n} as ranked; filtered to chunks that start in a table {starts}/{n}, that hold one {filtered}/{n}; those moved first {first}/{n}");
        }
    }

    /// <summary>Questions the router compares with, written for it: none from the golden set or the figure tasks.</summary>
    public static IReadOnlyList<(string Text, string Route)> RouterExamples { get; } =
    [
        ("What is the clearance between the impeller and the wear ring on a CP-200?", "table"),
        ("Which grease goes in the CP-200 bearings, and how much?", "table"),
        ("What is the maximum oil temperature for an HG-400?", "table"),
        ("What is the list price of the PN-3311-A bearing?", "table"),
        ("Which fault code means overvoltage on a VD-75 drive?", "table"),
        ("What torque applies to the casing bolts?", "table"),
        ("Show me the drawing of the VD-75 terminal block.", "figure"),
        ("Which number points to the drain plug in the gearbox diagram?", "figure"),
        ("Is there a picture of the seal-water strainer?", "figure"),
        ("What does the exploded view of the RX coupling show?", "figure"),
        ("Which part is labeled 4 in the pump drawing?", "figure"),
        ("Where on the diagram is the pressure switch?", "figure"),
        ("How do I replace the mechanical seal on a CP-200?", "text"),
        ("Why does the controller trip the pump when seal-water pressure is low?", "text"),
        ("What changed in ECN-2240, and why?", "text"),
        ("Who must approve a lockout on the night shift?", "text"),
        ("What should I check before restarting P-301 after maintenance?", "text"),
        ("What does safety bulletin SB-0932 ask technicians to do?", "text"),
    ];

    /// <summary>Rules: drawing words mean a figure; a number with a unit, or words that ask for one, mean a table.</summary>
    public static string Rules(string question) =>
        FigureWords().IsMatch(question) ? "figure" : TableWords().IsMatch(question) ? "table" : "text";

    [GeneratedRegex(@"\b(?:diagram|drawing|exploded view|figure|picture|photo|image|labell?ed|item \d+|number(?:ed)? \d+|shown)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FigureWords();

    [GeneratedRegex(@"\b(?:torque|how often|interval|how much|how many|price|cost|lead time|stock|rating|size|clearance|pressure|temperature|capacity|setting|voltage|current|speed|rpm|kW|part number|which (?:part|seal|bearing|oil|grease))\b", RegexOptions.IgnoreCase)]
    private static partial Regex TableWords();

    /// <summary>
    /// The lab: Tomás's torque question answered by Chapter 14's pipeline, and its citation shown on the scanned page:
    /// the quote found among the page's OCR words, the page rendered with that region highlighted, and a deep link.
    /// Then page-then-region search on its own: the page by BM25 over page text, the region by BM25 over that page's lines.
    /// </summary>
    public static async Task LabAsync(Lab lab, string id)
    {
        using var clip = Lab.Clip();
        var pages = await PageIndex.LoadAsync(lab, clip);
        var question = lab.Questions.Single(q => q.Id == id);
        var asker = People.Find(question.Asker)!;
        var answer = await (await lab.GeneratorAsync(Lab.AnswerChat())).AnswerAsync(question.Question, asker);
        Console.WriteLine($"{asker.Name}: {question.Question}\n{answer.Text}\n");
        foreach (var claim in answer.Claims.Where(c => c.SourceExists))
        {
            var block = answer.Evidence.First(b => b.Id == claim.Source);
            var source = lab.Sources[block.DocumentId];
            if (lab.Pdf(block.DocumentId) is not { } pdf || source.Projection.BlockAt(block.Segments[0].Start) is not { } at || source.Document.Blocks[at.Index].Anchor?.Page is not int page) continue;
            if (PageIndex.Locate(pages, pdf.Id, page, claim.Quote) is not var (unit, box))
            {
                Console.WriteLine($"[{claim.Source}] quote \"{claim.Quote}\": not found on {pdf.Id} page {page} or the next");
                continue;
            }

            Console.WriteLine(Invariant($"[{claim.Source}] quote \"{claim.Quote}\" → {pdf.Id} page {unit.Page}{(unit.Scanned ? " (scan, OCR words)" : "")}: box {box.Left:F0},{box.Top:F0} to {box.Right:F0},{box.Bottom:F0} pt"));
            Console.WriteLine($"  deep link {new VisualCitation(pdf.Id, unit.Page, box).Link}");
            var boxes = new List<BoundingBox> { box };

            // The cell-level citation: the table row in the evidence whose cells state the claim's number, on the same page.
            if (RowBehind(lab, block, claim.Text) is { } row && PageWords.Locate(unit.Words, string.Join(" | ", row)) is { } cells)
            {
                boxes.Add(cells);
                Console.WriteLine(Invariant($"  table row \"{string.Join(" | ", row)}\" → box {cells.Left:F0},{cells.Top:F0} to {cells.Right:F0},{cells.Bottom:F0} pt\n  deep link {new VisualCitation(pdf.Id, unit.Page, cells).Link}"));
            }

            var png = PageRenders.Highlight(pdf.Bytes, unit.Page, boxes);
            var file = Path.Combine(Lab.Runs, $"{id}-citation-p{unit.Page}.png");
            await File.WriteAllBytesAsync(file, png);
            Console.WriteLine($"  highlighted render {Path.GetFileName(file)} ({png.Length / 1024} KB)");
        }

        // Page then region, without the answer: which page, then which line on it.
        var bm25 = PageIndex.Bm25(pages);
        var ranked = PageIndex.Search("text", question.Question, asker, pages, bm25, clip);
        var top = pages[ranked[0]];
        var lines = new Bm25Index([.. top.Lines.Select(l => (IReadOnlyList<string>)SimpleAnalyzer.Terms(l.Text).ToList())]);
        var best = lines.Score(SimpleAnalyzer.Terms(question.Question)).OrderByDescending(s => s.Value).Take(3).Select(s => top.Lines[s.Key]).ToList();
        Console.WriteLine($"\nPage search: {string.Join(", ", ranked.Take(3).Select(i => $"{pages[i].Doc} p{pages[i].Page}"))}");
        foreach (var line in best) Console.WriteLine(Invariant($"  region \"{line.Text}\" at {line.Box.Left:F0},{line.Box.Top:F0} to {line.Box.Right:F0},{line.Box.Bottom:F0} pt"));
    }

    /// <summary>
    /// The table row behind a claim's number, for a cell-level citation: a data row of a table inside the cited
    /// evidence whose cells state every quantity the claim states, with the same units.
    /// </summary>
    public static IReadOnlyList<string>? RowBehind(Lab lab, EvidenceBlock evidence, string claim)
    {
        var claimed = NumericCheck.Quantities(claim);
        if (claimed.Count == 0) return null;
        return Tables(lab.Sources[evidence.DocumentId]).Where(t => evidence.Segments.Any(s => s.Start < t.Span.End && t.Span.Start < s.End))
            .SelectMany(t => TableSerializer.DataRows(t.Table))
            .FirstOrDefault(row => NumericCheck.Quantities(string.Join(" ", row)) is var stated && claimed.All(c => stated.Any(s => s.Value == c.Value && s.Unit == c.Unit)));
    }

    /// <summary>
    /// A small vision-language model on the CPU (Granite Vision 3.2 2B through Ollama): captions for the two figures and
    /// the torque table read straight from the scanned page's image, each with its time; replies cached by image and prompt.
    /// </summary>
    public static async Task VisionModelAsync(Lab lab)
    {
        using var clip = Lab.Clip();
        var pages = await PageIndex.LoadAsync(lab, clip);
        var chat = Lab.LiveChat("granite3.2-vision:2b");
        var cache = new Dictionary<string, (string Text, double Ms)>();
        var cacheFile = Path.Combine(Lab.Runs, "vlm-cache.jsonl");
        if (File.Exists(cacheFile))
        {
            foreach (var line in File.ReadLines(cacheFile)) { var e = JsonSerializer.Deserialize<VlmEntry>(line)!; cache[e.Key] = (e.Text, e.Ms); }
        }

        var figures = lab.Pdfs.SelectMany(p => p.Truth.Blocks.OfType<FigureBlock>().Select(f => (Pdf: p, Block: (Block)f, Prompt: "This is a figure from an equipment manual. Describe it, and list every numbered label with the part it points to.")));
        var table = lab.Pdf("CP200-MAN-2009")!;
        var torque = table.Truth.Blocks.OfType<TableBlock>().First(t => t.Rows.Any(r => r.Contains("M12")));
        var items = figures.Append((table, torque, "This is a table from a scanned pump manual. What torque applies to the M12 flange bolts? Answer with the value and unit only."))
            .Append((table, torque, "Transcribe this table as Markdown."));
        foreach (var (pdf, block, prompt) in items)
        {
            var anchor = block.Anchor!;
            var region = block is FigureBlock figure ? FigureRegion(pages.First(u => u.Doc == pdf.Id && u.Page == anchor.Page), figure) : anchor.Box!.Value;
            var png = Crop(pdf.Bytes, anchor.Page!.Value, region, maxSide: 768);
            var key = Convert.ToHexStringLower(SHA256.HashData([.. png, .. Encoding.UTF8.GetBytes(prompt)]));
            if (!cache.TryGetValue(key, out var reply))
            {
                var clock = Stopwatch.StartNew();
                var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, [new TextContent(prompt), new DataContent(png, "image/png")])],
                    new ChatOptions { Temperature = 0, MaxOutputTokens = 400 });
                reply = (response.Text.Trim(), clock.Elapsed.TotalMilliseconds);
                cache[key] = reply;
                await File.AppendAllTextAsync(cacheFile, JsonSerializer.Serialize(new VlmEntry(key, pdf.Id, anchor.Page.Value, prompt, reply.Text, reply.Ms)) + "\n");
            }

            Console.WriteLine($"{pdf.Id} page {anchor.Page} ({block.GetType().Name}), {reply.Ms / 1000:F1} s: {prompt}\n  {reply.Text.ReplaceLineEndings("\n  ")}\n");
        }
    }

    /// <summary>
    /// Where a figure is on its page: around its caption and its numbered labels, found among the page's words. The
    /// drawing sits between them; an anchor box from layout isn't always right (the HG-400's points at the other column).
    /// </summary>
    public static BoundingBox FigureRegion(PageUnit page, FigureBlock figure)
    {
        var boxes = figure.Labels.Prepend(figure.Caption ?? "").Select(t => PageWords.Locate(page.Words, t)).OfType<BoundingBox>().ToList();
        return new BoundingBox(boxes.Min(b => b.Left), boxes.Min(b => b.Top), boxes.Max(b => b.Right), boxes.Max(b => b.Bottom));
    }

    /// <summary>
    /// A region of a page with a margin, as a PNG, rendered at up to 150 DPI but no more than <paramref name="maxSide"/>
    /// pixels on its longer side: Granite Vision encodes an image as 729-token tiles, each about ten seconds on the CPU.
    /// </summary>
    public static byte[] Crop(byte[] pdf, int page, BoundingBox box, int maxSide, double margin = 18)
    {
        var dpi = (int)Math.Min(150, maxSide * 72 / (Math.Max(box.Width, box.Height) + 2 * margin));
        using var render = PageRenders.Render(pdf, page, dpi);
        var s = dpi / 72.0;
        var rect = SKRectI.Round(new SKRect((float)((box.Left - margin) * s), (float)((box.Top - margin) * s), (float)((box.Right + margin) * s), (float)((box.Bottom + margin) * s)));
        rect.Intersect(new SKRectI(0, 0, render.Width, render.Height));
        using var crop = new SKBitmap(rect.Width, rect.Height);
        render.ExtractSubset(crop, rect);
        using var image = SKImage.FromBitmap(crop);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private sealed record VlmEntry(string Key, string Doc, int Page, string Prompt, string Text, double Ms);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Squeezed(string text) => string.Concat(text.Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();
}
