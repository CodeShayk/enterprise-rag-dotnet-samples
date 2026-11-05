using System.Text.RegularExpressions;

namespace Contoso.Knowledge.Ocr;

/// <summary>Error rates against a reference text: edit distance over characters or words, divided by the reference length.</summary>
public static partial class OcrQuality
{
    /// <summary>Character error rate: (substitutions + deletions + insertions) / reference characters.</summary>
    public static double Cer(string reference, string hypothesis)
    {
        var r = Normalize(reference);
        var h = Normalize(hypothesis);
        return r.Length == 0 ? (h.Length == 0 ? 0 : 1) : (double)Distance(r.AsSpan(), h.AsSpan()) / r.Length;
    }

    /// <summary>Word error rate: the same over whitespace-separated words.</summary>
    public static double Wer(string reference, string hypothesis)
    {
        var r = Words(reference);
        var h = Words(hypothesis);
        return r.Length == 0 ? (h.Length == 0 ? 0 : 1) : (double)Distance<string>(r, h) / r.Length;
    }

    /// <summary>
    /// Lines in row-major order: lines whose vertical centers are within half a line height form one row, read
    /// left to right. Engines differ in what they call a line (PaddleOCR returns each table cell on its own), so
    /// comparing engines needs one ordering for both; layout analysis replaces this for real documents.
    /// </summary>
    public static string RowMajorText(IEnumerable<OcrLine> lines) =>
        string.Join('\n', RowMajor(lines).Select(r => string.Join(' ', r.Select(l => l.Text))));

    /// <summary>The rows of <see cref="RowMajorText"/>, each row's lines left to right.</summary>
    public static List<List<OcrLine>> RowMajor(IEnumerable<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var line in lines.OrderBy(l => l.Box.CenterY))
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(line.Box.CenterY - row.Average(l => l.Box.CenterY)) < 0.5 * Math.Min(line.Box.Height, row.Min(l => l.Box.Height)))
            {
                row.Add(line);
            }
            else
            {
                rows.Add([line]);
            }
        }

        return [.. rows.Select(r => r.OrderBy(l => l.Box.Left).ToList())];
    }

    /// <summary>
    /// Which hypothesis words match the reference, by a minimum-edit alignment of the two word sequences: a word
    /// counts as right only if it's aligned to an identical reference word.
    /// </summary>
    public static bool[] MatchedWords(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        var d = new int[reference.Count + 1, hypothesis.Count + 1];
        for (var i = 0; i <= reference.Count; i++) d[i, 0] = i;
        for (var j = 0; j <= hypothesis.Count; j++) d[0, j] = j;
        for (var i = 1; i <= reference.Count; i++)
        {
            for (var j = 1; j <= hypothesis.Count; j++)
            {
                var cost = reference[i - 1] == hypothesis[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        var matched = new bool[hypothesis.Count];
        for (int i = reference.Count, j = hypothesis.Count; i > 0 && j > 0;)
        {
            if (d[i, j] == d[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1))
            {
                matched[j - 1] = reference[i - 1] == hypothesis[j - 1];
                i--;
                j--;
            }
            else if (d[i, j] == d[i, j - 1] + 1) j--;
            else i--;
        }

        return matched;
    }

    public static bool IsMarker(string word) => Marker().IsMatch(word);

    /// <summary>Collapses whitespace and drops list markers, which are layout, not text.</summary>
    public static string Normalize(string text) => string.Join(' ', Words(text));

    public static string[] Words(string text) =>
        [.. text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(w => !Marker().IsMatch(w))];

    public static int Distance<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : IEquatable<T>
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1].Equals(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    [GeneratedRegex(@"^([•·▪\-–]|\d{1,2}[.)])$")]
    private static partial Regex Marker();
}
