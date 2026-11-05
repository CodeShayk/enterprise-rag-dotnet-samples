using System.Text;
using System.Text.RegularExpressions;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Parsing;
using Microsoft.ML.Tokenizers;

namespace Ch08.Ocr.Cli;

/// <summary>
/// OCR quality measured as retrieval: golden set v1's dev questions with evidence on scanned pages, ported to the
/// full corpus, answered by Chapter 5's BM25 over 100-token chunks of the corpus as each configuration reads it.
/// A span the OCR garbled can't be located, so its fact counts as not found, which is what a user would get.
/// Copied from Chapter 7 and extended to the scans.
/// </summary>
public static partial class RetrievalCheck
{
    /// <summary>Where each version 1 document's text lives in the full corpus, scans included.</summary>
    private static readonly Dictionary<string, string> Ported = new(StringComparer.Ordinal)
    {
        ["CP200-TRB-E104"] = "CP200-TRB-2019", ["CP200-TRB-E217"] = "CP200-TRB-2019", ["CP200-TRB-VIB"] = "CP200-TRB-2019",
        ["HG400-MAN-3.2"] = "HG400-MAN-2021", ["HG400-MAN-4.1"] = "HG400-MAN-2021", ["VFD-MAN-F012"] = "VD75-MAN-2022",
        ["CPL-RX-INST"] = "CPL-RX-INST", ["HR-PL-2024"] = "HR-PL-2024", ["HR-PL-2027"] = "HR-PL-2027", ["IT-POL-003"] = "IT-POL-003",
        ["P201-ASSET"] = "P201-ASSET", ["PROC-POL-007"] = "PROC-POL-007", ["SB-0932"] = "SB-0932", ["SB-0947"] = "SB-0947",
        ["SC-2024-118"] = "SC-2024-118", ["SOP-FS-014"] = "SOP-FS-014", ["SR-2026-1107"] = "SR-2026-1107", ["SR-2026-1213"] = "SR-2026-1213",
        ["SVC-POL-002"] = "SVC-POL-002", ["WI-FS-031"] = "WI-FS-031",
        ["CP200-MAN-2009-5.4"] = "CP200-MAN-2009", ["CP200-MAN-2009-6.3"] = "CP200-MAN-2009", ["CP200-MAN-2009-7.2"] = "CP200-MAN-2009",
        ["ECN-2291"] = "ECN-2291",
    };

    /// <summary>The version 1 documents that exist only as scans.</summary>
    private static readonly HashSet<string> Scanned = ["CP200-MAN-2009-5.4", "CP200-MAN-2009-6.3", "CP200-MAN-2009-7.2", "ECN-2291"];

    public sealed record ModeResult(string Mode, int SpansFound, int Spans, IReadOnlyList<double> Recall, IReadOnlyList<double> Sufficient);

    /// <param name="ocr">False to search the corpus as Chapter 7 left it: scanned files can't be searched.</param>
    public static async Task<ModeResult> RunAsync(string mode, ParserRegistry registry, IReadOnlyList<GoldenQuestion> questions, Tokenizer tokenizer, bool ocr)
    {
        // One searchable document per logical document: no copies and current versions only.
        var corpus = new Dictionary<string, SourceDocument>(StringComparer.Ordinal);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            var truth = await GeneratedCorpus.TruthAsync(entry);
            if (!ocr && truth.Pages.Count > 0 && truth.Pages.All(p => !p.HasTextLayer))
            {
                corpus[entry.Id] = new SourceDocument(entry.Id, truth.Metadata.Title, truth.Metadata.Tenant, truth.Metadata.AllowedGroups, "");
                continue;
            }

            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;   // the archived revision 2
            await using var stream = File.OpenRead(GeneratedCorpus.FullPath(entry));
            var parsed = await registry.ParseAsync(stream, new ParseContext(Path.GetFileName(entry.Path), entry.MediaType));
            var document = new CanonicalDocument(entry.Id, entry.Version, truth.Metadata, truth.Source, parsed.Blocks);
            corpus[entry.Id] = new SourceDocument(entry.Id, truth.Metadata.Title, truth.Metadata.Tenant, truth.Metadata.AllowedGroups, document.ToText().Text);
        }

        var retriever = new Bm25(Chunk([.. corpus.Values], tokenizer, 100, 20));
        int found = 0, total = 0;
        var recall = new List<double>();
        var sufficient = new List<double>();
        foreach (var question in questions)
        {
            var asker = People.Find(question.Asker)!;
            var top = retriever.Search(question.Question, asker, 4);
            var spans = question.Relevant.Where(s => s.Grade == question.Relevant.Max(r => r.Grade)).ToList();
            var facts = spans.GroupBy(s => s.Fact).ToList();
            var covered = 0;
            var whole = 0;
            foreach (var fact in facts)
            {
                var located = fact.Select(s => Locate(corpus[Ported[s.Doc]], s.Text)).ToList();
                total += located.Count;
                found += located.Count(l => l is not null);
                var hits = located.OfType<ChunkSpan>().ToList();
                if (hits.Any(span => top.Any(c => c.Doc == span.Doc && Overlap(c, span) * 2 >= span.Length))) covered++;
                if (hits.Any(span => top.Any(c => c.Doc == span.Doc && c.Start <= span.Start && c.End >= span.End))) whole++;
            }

            recall.Add((double)covered / facts.Count);
            sufficient.Add(whole == facts.Count ? 1 : 0);
        }

        return new ModeResult(mode, found, total, recall, sufficient);
    }

    /// <summary>The questions to use: dev split, answerable, every span ported, and some evidence only on a scan.</summary>
    public static IReadOnlyList<GoldenQuestion> Questions() =>
        [.. GoldenSet.Load().Where(q => q.Split == "dev" && q.Answerable && q.Relevant.All(s => Ported.ContainsKey(s.Doc)) && q.Relevant.Any(s => Scanned.Contains(s.Doc)))];

    /// <summary>Finds a span verbatim, then ignoring whitespace differences; null when the parser lost it.</summary>
    private static ChunkSpan? Locate(SourceDocument document, string span)
    {
        var at = document.Text.IndexOf(span, StringComparison.Ordinal);
        if (at >= 0) return new ChunkSpan(document.Id, at, at + span.Length);
        var (text, map) = Collapse(document.Text);
        var (needle, _) = Collapse(span);
        at = text.IndexOf(needle, StringComparison.Ordinal);
        return at < 0 ? null : new ChunkSpan(document.Id, map[at], map[at + needle.Length - 1] + 1);
    }

    private static (string Text, int[] Map) Collapse(string s)
    {
        var text = new StringBuilder(s.Length);
        var map = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                if (text.Length > 0 && text[^1] != ' ') { text.Append(' '); map.Add(i); }
                continue;
            }

            text.Append(s[i]);
            map.Add(i);
        }

        return (text.ToString(), [.. map]);
    }

    private static int Overlap(ChunkSpan a, ChunkSpan b) => Math.Max(0, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start));

    private sealed record Indexed(SourceDocument Document, ChunkSpan Span, string Text);

    // Chapter 5's fixed-size chunker and BM25, copied here because chapters don't reference each other.
    private static List<Indexed> Chunk(IReadOnlyList<SourceDocument> corpus, Tokenizer tokenizer, int chunkTokens, int overlapTokens)
    {
        var chunks = new List<Indexed>();
        foreach (var document in corpus)
        {
            var text = document.Text;
            var start = 0;
            while (start < text.Length)
            {
                var length = tokenizer.GetIndexByTokenCount(text[start..], chunkTokens, out _, out _);
                chunks.Add(new Indexed(document, new ChunkSpan(document.Id, start, start + length), text.Substring(start, length)));
                if (start + length >= text.Length) break;
                start += Math.Max(tokenizer.GetIndexByTokenCountFromEnd(text.Substring(start, length), overlapTokens, out _, out _), 1);
            }
        }

        return chunks;
    }

    private sealed class Bm25
    {
        private const double K1 = 1.2, B = 0.75;
        private readonly List<Indexed> _chunks;
        private readonly List<Dictionary<string, int>> _counts;
        private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);
        private readonly double _averageLength;

        public Bm25(List<Indexed> chunks)
        {
            _chunks = chunks;
            _counts = [.. chunks.Select(c => Terms(c.Text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal))];
            foreach (var term in _counts.SelectMany(c => c.Keys)) _df[term] = _df.GetValueOrDefault(term) + 1;
            _averageLength = _counts.Average(c => c.Values.Sum());
        }

        public IReadOnlyList<ChunkSpan> Search(string question, Person asker, int top)
        {
            var terms = Terms(question).Distinct().ToList();
            return [.. Enumerable.Range(0, _chunks.Count).Where(i => asker.CanRead(_chunks[i].Document))
                .Select(i => (i, score: Score(terms, i))).Where(r => r.score > 0).OrderByDescending(r => r.score).Take(top)
                .Select(r => _chunks[r.i].Span)];
        }

        private double Score(List<string> terms, int i)
        {
            var length = _counts[i].Values.Sum();
            double score = 0;
            foreach (var term in terms)
            {
                if (!_counts[i].TryGetValue(term, out var tf)) continue;
                var n = _df[term];
                score += Math.Log(1 + (_chunks.Count - n + 0.5) / (n + 0.5)) * tf * (K1 + 1) / (tf + K1 * (1 - B + B * length / _averageLength));
            }

            return score;
        }

        private static List<string> Terms(string text) =>
            [.. Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => !ContosoScripts.IsStopWord(w)).Select(Stem)];

        private static string Stem(string word) => word switch
        {
            _ when word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal) => word[..^3],
            _ when word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal) => word[..^3] + "y",
            _ when word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) => word[..^1],
            _ => word,
        };
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-.'][\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
