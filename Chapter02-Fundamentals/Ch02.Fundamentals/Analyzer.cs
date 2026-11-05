using System.Text.RegularExpressions;

namespace Ch02.Fundamentals;

/// <summary>Turns text into index terms: lowercase words, IDs kept whole, stop words dropped, optionally stemmed.</summary>
public static partial class Analyzer
{
    public static List<string> Terms(string text, bool stem = false) =>
        Word().Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(word => !StopWords.Contains(word))
            .Select(word => stem ? Stem(word) : word)
            .ToList();

    /// <summary>A deliberately tiny English stemmer for plurals and -ing. Chapter 11 uses a real analyzer.</summary>
    public static string Stem(string word) => word switch
    {
        _ when word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal) => word[..^3],
        _ when word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal) => word[..^3] + "y",
        _ when word.Length > 4 && (word.EndsWith("sses", StringComparison.Ordinal) || word.EndsWith("ches", StringComparison.Ordinal)
            || word.EndsWith("shes", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal)) => word[..^2],
        _ when word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) => word[..^1],
        _ => word,
    };

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in", "is",
        "it", "its", "me", "my", "now", "of", "on", "or", "our", "should", "that", "the", "this", "to", "we", "what",
        "when", "where", "which", "who", "why", "with", "you", "your",
    ];

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-.'][\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
