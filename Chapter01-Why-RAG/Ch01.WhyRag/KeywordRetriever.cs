using System.Text.RegularExpressions;
using Contoso.Corpus;

namespace Ch01.WhyRag;

/// <summary>A retrieved document and how many of the question's words it contains.</summary>
public sealed record Hit(SourceDocument Document, int Score);

/// <summary>
/// The simplest retriever that still behaves like an enterprise one. It drops every document the
/// asker may not read, scores the rest by how many of the question's distinct words they contain,
/// and keeps only documents that share at least <paramref name="minimumScore"/> words.
/// Chapter 2 explains how real retrievers score text, and Chapter 11 builds one.
/// </summary>
public sealed partial class KeywordRetriever(IReadOnlyList<SourceDocument> documents, int minimumScore = 2)
{
    public IReadOnlyList<Hit> Retrieve(string question, Person asker, int top = 3)
    {
        var terms = Terms(question);
        return documents
            .Where(asker.CanRead)
            .Select(document => new Hit(document, Terms($"{document.Title} {document.Text}").Count(terms.Contains)))
            .Where(hit => hit.Score >= minimumScore)
            .OrderByDescending(hit => hit.Score)
            .Take(top)
            .ToList();
    }

    /// <summary>The lowercase words of a text, without stop words. IDs such as P-201 stay whole.</summary>
    public static HashSet<string> Terms(string text) =>
        Word().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(word => !StopWords.Contains(word))
            .ToHashSet();

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in", "is",
        "it", "its", "me", "my", "now", "of", "on", "or", "our", "should", "that", "the", "this", "to", "we", "what",
        "when", "where", "which", "who", "why", "with", "you", "your",
    ];

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-.'][\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
