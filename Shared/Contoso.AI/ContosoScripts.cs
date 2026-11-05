using System.Text.RegularExpressions;

namespace Contoso.AI;

/// <summary>
/// The scripted model's default behavior. When the prompt carries numbered sources, it answers
/// extractively: it copies the sentences of source [1] that share a word with the question and
/// cites that source. Without sources it gives the generic answer a general-purpose model gives
/// from public knowledge. It's a deterministic stand-in for a model, so outputs are reproducible.
/// </summary>
public static partial class ContosoScripts
{
    /// <summary>Answers from the first numbered source in a "Sources: ... Question: ..." prompt.</summary>
    public static ScriptRule Grounded { get; } = new("grounded", Reply: context =>
    {
        var question = Question().Match(context.UserText);
        var source = FirstSource().Match(context.UserText);
        if (!question.Success || !source.Success)
        {
            return null;
        }

        // Sentences and lines (a table row, a list item) are the units it copies; headings are skipped.
        var terms = Terms(question.Groups["text"].Value);
        var sentences = SentenceBreak().Split(source.Groups["text"].Value.Trim())
            .Where(sentence => !sentence.StartsWith('#') && Terms(sentence).Overlaps(terms))
            .ToList();

        return sentences.Count == 0
            ? ScriptReply.Say("The sources don't answer that question.")
            : ScriptReply.Say($"{string.Join(" ", sentences)} [{source.Groups["n"].Value}]");
    });

    /// <summary>What a model with no access to Contoso's documents says about flange bolt torque.</summary>
    public static ScriptRule GeneralKnowledge { get; } = new("general-knowledge", Reply: context =>
        context.Mentions("torque") && context.Mentions("bolt")
            ? ScriptReply.Say(
                "For M12 flange bolts, a typical tightening torque is between 60 and 90 N·m, depending on the bolt grade, " +
                "the lubrication and the gasket. Check the pump manufacturer's documentation for the exact value.")
            : null);

    public static ScriptReply Fallback { get; } = ScriptReply.Say("I don't have information about that.");

    public static IReadOnlyList<ScriptRule> Default { get; } = [Grounded, GeneralKnowledge];

    /// <summary>The lowercase words of a text, without common stop words. IDs such as PN-4471-C stay whole.</summary>
    public static HashSet<string> Terms(string text) =>
        Word().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w))
            .ToHashSet();

    /// <summary>Whether a lowercase word is too common to say anything about a text.</summary>
    public static bool IsStopWord(string word) => StopWords.Contains(word);

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in", "is",
        "it", "its", "me", "my", "now", "of", "on", "or", "our", "should", "that", "the", "this", "to", "we", "what",
        "when", "where", "which", "who", "why", "with", "you", "your",
    ];

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-.'][\p{L}\p{N}]+)*")]
    private static partial Regex Word();

    // After a sentence's final punctuation, or at a line break.
    [GeneratedRegex(@"(?<=[.!?])\s+|\s*\n\s*")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"^Question:\s*(?<text>.+)$", RegexOptions.Multiline)]
    private static partial Regex Question();

    // "[1] Title (ID)" on one line, then the source's text up to the next blank line.
    [GeneratedRegex(@"^\[(?<n>\d+)\][^\n]*\n(?<text>(?:.+\n?)+?)(?:\n|$)", RegexOptions.Multiline)]
    private static partial Regex FirstSource();
}
