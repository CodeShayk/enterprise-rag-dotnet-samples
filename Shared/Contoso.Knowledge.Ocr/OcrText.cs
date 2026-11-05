using System.Text.RegularExpressions;

namespace Contoso.Knowledge.Ocr;

/// <summary>
/// Post-correction: a few rules for errors that are systematic and whose right answer the domain fixes. Each
/// rule is narrow, so it can't invent text, and every correction is counted, because a corrected word is still
/// a word the engine misread. Add rules from the review queue, not from guesses.
/// </summary>
public static partial class OcrText
{
    private static readonly (Regex Pattern, string Replacement, string Name)[] Rules =
    [
        // "4. 1 Foundation": engines read the dot in a section number as the end of a sentence.
        (SectionNumber(), "$1.$2", "section number"),

        // "85 N-m", "85 N'm": Contoso writes newton-metres with a middle dot, which engines read as a hyphen, an
        // apostrophe or a full stop.
        (Torque(), "$1 N·m", "N·m"),
    ];

    public static string Correct(string text) => Correct(text, out _);

    public static string Correct(string text, out int corrections)
    {
        corrections = 0;
        foreach (var (pattern, replacement, _) in Rules)
        {
            var count = pattern.Count(text);
            if (count == 0) continue;
            corrections += count;
            text = pattern.Replace(text, replacement);
        }

        return text;
    }

    /// <summary>Table rules read as text: a cell that starts or ends with a vertical bar.</summary>
    public static string Cell(string text) => text.Trim().Trim('|', '[', ']').Trim();

    [GeneratedRegex(@"^(\d{1,2})\. (\d{1,2})(?=\s)")]
    private static partial Regex SectionNumber();

    [GeneratedRegex(@"(\d) ?N[-'.:’]m\b")]
    private static partial Regex Torque();
}
