using System.Diagnostics;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Ocr;
using Contoso.Knowledge.Retrieval;
using Contoso.Knowledge.Vision;

namespace Ch18.Multimodal.Cli;

/// <summary>A line OCR or the text layer found on a page, with its box in points.</summary>
public sealed record PageLine(string Text, BoundingBox Box);

/// <summary>
/// One PDF page as a retrieval unit: its text (the text layer, or OCR on a scan), its lines and words with boxes,
/// its CLIP image vector, who may read it and what each representation cost to build.
/// </summary>
public sealed record PageUnit(string Doc, int Page, bool Scanned, string Text, IReadOnlyList<PageLine> Lines, IReadOnlyList<PageWord> Words,
    float[] Image, IReadOnlyList<string> AllowedGroups, double TextMs, double RenderMs, double ClipMs);

/// <summary>A question about a figure, written for this chapter: the golden set has none.</summary>
public sealed record FigureTask(string Id, string Asker, string Question, string Doc, int Page, string Answer);

public static class PageIndex
{
    public static IReadOnlyList<FigureTask> FigureTasks { get; } =
    [
        new("F1", "tomas.silva", "Show me the exploded view of the CP-200 seal housing.", "CP200-MAN-2009", 4, "Figure 8-1"),
        new("F2", "tomas.silva", "Which item number is the shaft sleeve in the seal housing drawing?", "CP200-MAN-2009", 4, "4"),
        new("F3", "nadia.rahman", "What number do the gland nuts have in the CP-200 exploded view?", "CP200-MAN-2009", 4, "2"),
        new("F4", "kwame.asante", "Is there a diagram of the HG-400 gearbox that shows the breather plug?", "HG400-MAN-2021", 1, "5"),
        new("F5", "kwame.asante", "Which number marks the sight glass on the HG-400 exploded view?", "HG400-MAN-2021", 1, "6"),
        new("F6", "kwame.asante", "What part is item 3 in the gearbox drawing?", "HG400-MAN-2021", 1, "Pinion"),
    ];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// Every PDF page of the corpus as a unit, built once and cached in runs/ch18/pages.json: text and words from the
    /// text layer (PdfPig) or from Tesseract on the prepared scan (Chapter 8), and a CLIP vector of the page rendered at 96 DPI.
    /// </summary>
    public static async Task<IReadOnlyList<PageUnit>> LoadAsync(Lab lab, ClipModel clip)
    {
        var cache = Path.Combine(Lab.Runs, "pages.json");
        if (File.Exists(cache)) return JsonSerializer.Deserialize<List<PageUnit>>(await File.ReadAllTextAsync(cache))!;
        using var ocr = Lab.Tesseract();
        var pages = new List<PageUnit>();
        foreach (var pdf in lab.Pdfs)
        {
            foreach (var triage in pdf.Pages)
            {
                var clock = Stopwatch.StartNew();
                var scanned = triage.Kind == PageKind.Scanned;
                var (lines, words) = scanned ? Ocr(pdf.Bytes, triage.Page, ocr) : Digital(pdf.Bytes, triage.Page);
                var textMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                using var render = PageRenders.Render(pdf.Bytes, triage.Page, 96);
                var renderMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var image = clip.EmbedImage(render);
                pages.Add(new PageUnit(pdf.Id, triage.Page, scanned, string.Join('\n', lines.Select(l => l.Text)), lines, words, image,
                    pdf.Truth.Metadata.AllowedGroups, textMs, renderMs, clock.Elapsed.TotalMilliseconds));
                Console.Error.WriteLine($"  {pdf.Id} p{triage.Page}{(scanned ? " (scan)" : "")}: text {textMs:F0} ms, render {renderMs:F0} ms, CLIP {pages[^1].ClipMs:F0} ms");
            }
        }

        Directory.CreateDirectory(Lab.Runs);
        await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(pages, Json));
        return pages;
    }

    private static (List<PageLine>, List<PageWord>) Digital(byte[] pdf, int page)
    {
        var words = PageWords.Digital(pdf, page).ToList();
        return ([.. PageWords.Lines(words).Select(l => new PageLine(string.Join(' ', l.Select(w => w.Text)),
            new BoundingBox(l.Min(w => w.Box.Left), l.Min(w => w.Box.Top), l.Max(w => w.Box.Right), l.Max(w => w.Box.Bottom))))], words);
    }

    private static (List<PageLine>, List<PageWord>) Ocr(byte[] pdf, int page, IOcrEngine engine)
    {
        using var image = PageImages.Render(pdf, page, 300);
        using var prepared = ImagePrep.Prepare(image, 300, PrepOptions.Default);
        var read = engine.Recognize(prepared.Image);
        return ([.. read.Lines.Select(l => new PageLine(OcrText.Correct(l.Text, out _), prepared.ToPagePoints(l.Box)))],
            [.. read.Lines.SelectMany(l => l.Words).Select(w => new PageWord(w.Text, prepared.ToPagePoints(w.Box)))]);
    }

    /// <summary>The pages a golden question's labeled spans were laid out on, from the truth's anchors; empty if any span isn't in a PDF.</summary>
    public static IReadOnlyList<(string Doc, int Page)> Targets(Lab lab, GoldenQuestion question)
    {
        var spans = lab.Spans(question);
        if (spans.Count == 0 || spans.Any(s => lab.Pdf(s.Doc) is null)) return [];
        return [.. spans.Select(s => (s.Doc, Page: lab.BlockOf(s)?.Anchor?.Page)).Where(t => t.Page is not null).Select(t => (t.Doc, t.Page!.Value)).Distinct()];
    }

    /// <summary>
    /// Where a quote is: on the page its block starts on (the block's anchor), or on the next page, since a table or a
    /// paragraph that reaches the bottom of a page carries on over the break.
    /// </summary>
    public static (PageUnit Page, BoundingBox Box)? Locate(IReadOnlyList<PageUnit> pages, string doc, int page, string quote) =>
        pages.Where(u => u.Doc == doc && (u.Page == page || u.Page == page + 1)).OrderBy(u => u.Page)
            .Select(u => (u, PageWords.Locate(u.Words, quote))).Where(x => x.Item2 is not null).Select(x => ((PageUnit, BoundingBox)?)(x.u, x.Item2!.Value))
            .FirstOrDefault();

    /// <summary>Ranked pages for a question by one representation, among the pages the asker may read.</summary>
    public static IReadOnlyList<int> Search(string method, string question, Person asker, IReadOnlyList<PageUnit> pages, Bm25Index bm25, ClipModel clip)
    {
        var readable = Enumerable.Range(0, pages.Count).Where(i => pages[i].AllowedGroups.Any(asker.Groups.Contains)).ToHashSet();
        IReadOnlyList<int> Text()
        {
            var scores = bm25.Score(SimpleAnalyzer.Terms(question));
            return [.. scores.Where(s => readable.Contains(s.Key)).OrderByDescending(s => s.Value).Select(s => s.Key)];
        }

        IReadOnlyList<int> Image()
        {
            var q = clip.EmbedText(question);
            return [.. readable.OrderByDescending(i => ClipModel.Similarity(q, pages[i].Image))];
        }

        return method switch
        {
            "text" => Text(),
            "image" => Image(),
            "fused" => Fuse(Text(), Image()),
            _ => throw new ArgumentException(method),
        };
    }

    /// <summary>Reciprocal rank fusion with k = 60 (Chapter 11).</summary>
    public static IReadOnlyList<int> Fuse(params IReadOnlyList<int>[] rankings) =>
        [.. rankings.SelectMany(r => r.Select((page, rank) => (page, score: 1.0 / (60 + rank + 1)))).GroupBy(x => x.page)
            .OrderByDescending(g => g.Sum(x => x.score)).Select(g => g.Key)];

    public static Bm25Index Bm25(IReadOnlyList<PageUnit> pages) => new([.. pages.Select(p => (IReadOnlyList<string>)SimpleAnalyzer.Terms(p.Text).ToList())]);
}
