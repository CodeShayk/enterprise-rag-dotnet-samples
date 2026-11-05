using System.Security.Cryptography;
using System.Text.Json;
using Contoso.Corpus;

namespace Contoso.Knowledge.Evaluation;

/// <summary>
/// A passage that answers a question, quoted verbatim from its source document. Grade 2 answers
/// the question (or one part of it); grade 1 helps. Spans with the same <see cref="Fact"/> are
/// alternatives: any one of them supplies that fact.
/// </summary>
public sealed record RelevantSpan(string Doc, string Text, int Grade = 2, string Fact = "a");

/// <summary>One labeled question: one JSON object per line of the golden set.</summary>
public sealed record GoldenQuestion(
    string Id,
    string Asker,
    string Question,
    string Type,
    string Source,
    string Split,
    IReadOnlyList<RelevantSpan> Relevant,
    IReadOnlyList<string>? Tags = null,
    string? Answer = null)
{
    /// <summary>An empty <see cref="Relevant"/> list means the right response is an abstention.</summary>
    public bool Answerable => Relevant.Count > 0;
}

/// <summary>A labeled span found in its document: the character range a chunk must overlap.</summary>
public sealed record LocatedSpan(string Doc, int Start, int End, int Grade, string Fact)
{
    public int Length => End - Start;
}

public static class GoldenSet
{
    public static string DefaultPath { get; } = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v1.jsonl");

    public static IReadOnlyList<GoldenQuestion> Load(string? path = null) =>
        [.. File.ReadLines(path ?? DefaultPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<GoldenQuestion>(line, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException($"Unreadable golden-set line: {line}"))];

    /// <summary>The file's SHA-256, recorded with every run so that results name the exact labels they used.</summary>
    public static string Sha256(string? path = null) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path ?? DefaultPath)));

    /// <summary>
    /// Finds every labeled span in the current text of its document. A span that isn't found, or that
    /// sits in a document the asker can't read, is a labeling error, and it fails loudly.
    /// </summary>
    public static IReadOnlyList<LocatedSpan> Locate(GoldenQuestion question, IReadOnlyDictionary<string, SourceDocument> documents)
    {
        var asker = People.Find(question.Asker) ?? throw new InvalidDataException($"{question.Id}: unknown asker '{question.Asker}'.");
        var located = new List<LocatedSpan>(question.Relevant.Count);
        foreach (var span in question.Relevant)
        {
            if (!documents.TryGetValue(span.Doc, out var document))
            {
                throw new InvalidDataException($"{question.Id}: no document '{span.Doc}'.");
            }

            if (!asker.CanRead(document))
            {
                throw new InvalidDataException($"{question.Id}: {asker.Name} can't read {span.Doc}, so it can't be evidence.");
            }

            var start = document.Text.IndexOf(span.Text, StringComparison.Ordinal);
            if (start < 0)
            {
                throw new InvalidDataException($"{question.Id}: span not found in {span.Doc}: \"{span.Text}\"");
            }

            located.Add(new LocatedSpan(span.Doc, start, start + span.Text.Length, span.Grade, span.Fact));
        }

        return located;
    }

    /// <summary>Document-level judgments in TREC qrels format: query, 0, document, grade.</summary>
    public static IEnumerable<string> ToTrecQrels(IEnumerable<GoldenQuestion> questions) =>
        questions.SelectMany(q => q.Relevant
            .GroupBy(span => span.Doc)
            .Select(doc => $"{q.Id} 0 {doc.Key} {doc.Max(span => span.Grade)}"));
}
