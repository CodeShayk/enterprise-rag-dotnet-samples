using System.Text.RegularExpressions;
using Contoso.Knowledge.Documents;

namespace Ch07.Parsing.Cli;

/// <summary>How faithfully one parse reproduces the truth a file was rendered from.</summary>
public sealed record ParseScore(
    string Path, string Format, int TruthWords,
    double WordRecall, double WordPrecision, double Order, double Sentences,
    int SentencesTotal, double TableCells, int Tables, int Leaks, IReadOnlyList<string> Lost);

/// <summary>
/// The parser evaluation checklist as numbers. Words are compared as bags (what was lost, what was added) and as
/// a sequence (reading order); sentences must survive verbatim, because that's what a golden-set span needs;
/// tables are compared cell by cell; running headers and footers must not leak into the body text.
/// </summary>
public static partial class ParserEvaluation
{
    public static ParseScore Score(string path, string format, CanonicalDocument truth, CanonicalDocument parsed)
    {
        var truthText = truth.ToText().Text;
        var parsedText = parsed.ToText().Text;
        var truthWords = Words(truthText);
        var parsedWords = Words(parsedText);

        var (recall, precision) = BagOverlap(truthWords, parsedWords);
        var order = truthWords.Count == 0 ? 1 : (double)Lcs(truthWords, parsedWords) / truthWords.Count;

        // Sentences of four words or more from the body, which is what golden-set spans are made of.
        var normalizedParsed = Collapse(parsedText);
        var sentences = truth.Blocks
            .Where(b => b.Role is null or BlockRoles.SpeakerNotes && b is ParagraphBlock or ListItemBlock or HeadingBlock)
            .SelectMany(b => SentenceSplit().Split(b switch { ParagraphBlock p => p.Text, ListItemBlock l => l.Text, HeadingBlock h => h.Text, _ => "" }))
            .Select(Collapse).Where(s => s.Split(' ').Length >= 4).ToList();
        var lost = sentences.Where(s => !normalizedParsed.Contains(s, StringComparison.Ordinal)).ToList();

        var tables = truth.Blocks.OfType<TableBlock>().Where(t => t.Role is null).ToList();
        var parsedTables = parsed.Blocks.OfType<TableBlock>().Where(t => t.Role is null).ToList();
        var cells = tables.Count == 0 ? 1 : tables.Average(t => parsedTables.Select(p => CellAccuracy(t, p)).DefaultIfEmpty(0).Max());

        // Running text that the parser left in the body: each truth header or footer line found in the parsed text.
        var running = truth.Blocks.Where(b => b.Role is BlockRoles.Header or BlockRoles.Footer)
            .Select(b => Collapse(((ParagraphBlock)b).Text)).Distinct().ToList();
        var leaks = running.Sum(r => Count(normalizedParsed, r));

        return new ParseScore(path, format, truthWords.Count, recall, precision, order,
            sentences.Count == 0 ? 1 : 1 - (double)lost.Count / sentences.Count, sentences.Count, cells, tables.Count, leaks, lost);
    }

    /// <summary>Content words only: Markdown syntax (#, |, ---, list markers) is projection, not content.</summary>
    public static List<string> Words(string text) =>
        [.. text.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(w => !Syntax().IsMatch(w))];

    private static (double Recall, double Precision) BagOverlap(List<string> truth, List<string> parsed)
    {
        var counts = truth.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var matched = 0;
        foreach (var word in parsed)
        {
            if (counts.TryGetValue(word, out var n) && n > 0)
            {
                counts[word] = n - 1;
                matched++;
            }
        }

        return (truth.Count == 0 ? 1 : (double)matched / truth.Count, parsed.Count == 0 ? 0 : (double)matched / parsed.Count);
    }

    /// <summary>The longest common subsequence of two word lists: words that appear in the same order in both.</summary>
    public static int Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 1; j <= b.Count; j++)
            {
                current[j] = a[i - 1] == b[j - 1] ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Count];
    }

    private static double CellAccuracy(TableBlock truth, TableBlock parsed)
    {
        var total = truth.Rows.Sum(r => r.Count);
        if (total == 0) return 1;
        var matched = 0;
        for (var r = 0; r < Math.Min(truth.Rows.Count, parsed.Rows.Count); r++)
        {
            for (var c = 0; c < Math.Min(truth.Rows[r].Count, parsed.Rows[r].Count); c++)
            {
                if (Collapse(truth.Rows[r][c]) == Collapse(parsed.Rows[r][c])) matched++;
            }
        }

        return (double)matched / total;
    }

    private static int Count(string text, string value)
    {
        if (value.Length == 0) return 0;
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    public static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(#+|\|+|(---\|)+|\|?(-{3}\|)+|-|\d+\.|•)$")]
    private static partial Regex Syntax();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z])")]
    private static partial Regex SentenceSplit();
}
