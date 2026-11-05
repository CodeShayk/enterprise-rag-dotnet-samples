using System.Text;
using System.Text.RegularExpressions;

namespace Contoso.Knowledge.Parsing;

/// <summary>Repairs that every extracted text needs: ligatures, odd spaces, broken lines and hyphenated line ends.</summary>
public static partial class TextCleanup
{
    /// <summary>
    /// Expands ligatures (ﬁ, ﬂ) and other compatibility characters, turns non-breaking and other odd spaces
    /// into plain spaces and collapses runs of whitespace. Characters such as ° and · are kept: NFKC is applied
    /// only to the compatibility ranges that cause trouble, because it would also rewrite "m³" as "m3".
    /// </summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is >= 'ﬀ' and <= 'ﬆ')
            {
                builder.Append(c.ToString().Normalize(NormalizationForm.FormKC));
            }
            else if (c is ' ' or ' ' or ' ' or ' ' or '\t')
            {
                builder.Append(' ');
            }
            else if (c is '­' or '​' or '﻿')
            {
                // Soft hyphens and zero-width characters: invisible on the page, harmful in the index.
            }
            else
            {
                builder.Append(c);
            }
        }

        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    /// <summary>
    /// Joins lines into running text. A line ending in a hyphen after a letter, followed by a line starting with a
    /// lowercase letter, was hyphenated by the typesetter: the hyphen goes. Other hyphens ("seal-water") stay.
    /// </summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (text.Length == 0)
            {
                text.Append(line);
            }
            else if (text.Length >= 2 && text[^1] == '-' && char.IsLetter(text[^2]) && char.IsLower(line[0]) && IsTypesetterBreak(text))
            {
                text.Length--;
                text.Append(line);
            }
            else
            {
                text.Append(' ').Append(line);
            }
        }

        return text.ToString();
    }

    // A real compound ("seal-", "start-") is kept when the word before the hyphen is a known prefix of one.
    private static bool IsTypesetterBreak(StringBuilder text)
    {
        var start = text.Length - 2;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        var fragment = text.ToString(start, text.Length - 1 - start).ToLowerInvariant();
        return !CompoundPrefixes.Contains(fragment);
    }

    private static readonly HashSet<string> CompoundPrefixes = new(StringComparer.Ordinal)
    {
        "seal", "start", "spiral", "end", "self", "non", "re", "co", "pre", "low", "high", "dry", "two", "three", "food",
        "lithium", "carbon", "silicon", "anti", "cross", "half", "over", "under", "fixed", "full", "multi", "follow",
    };

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();
}
