using System.Text.RegularExpressions;

namespace Contoso.Knowledge.Chunking;

/// <summary>
/// Sentences with their offsets. A full stop followed by a capital ends a sentence, and so does a line break,
/// because in the projection a line break ends a list item, a table row or a heading. Abbreviations such as
/// "e.g." and numbers such as "8.8" don't, because no capital follows, and neither does a list marker such as
/// "6." at the start of a line.
/// </summary>
public static partial class Sentences
{
    public static IReadOnlyList<(int Start, int End)> Split(string text, int start = 0, int end = -1)
    {
        if (end < 0) end = text.Length;
        var spans = new List<(int, int)>();
        var from = start;
        foreach (Match m in Boundary().Matches(text[start..end]))
        {
            Add(spans, text, from, start + m.Index);
            from = start + m.Index + m.Length;
        }

        Add(spans, text, from, end);
        return spans;
    }

    private static void Add(List<(int, int)> spans, string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (end > start) spans.Add((start, end));
    }

    [GeneratedRegex(@"(?<=[.!?])(?<!^[ \t]*(?:\d{1,3}|[a-z])\.)[ \t]+(?=[A-Z0-9""“(])|\n+", RegexOptions.Multiline)]
    private static partial Regex Boundary();
}
