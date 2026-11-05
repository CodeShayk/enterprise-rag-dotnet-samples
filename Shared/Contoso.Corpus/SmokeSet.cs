using System.Text.Json;

namespace Contoso.Corpus;

/// <summary>
/// One smoke-set question: who asks it, what they ask and which source documents hold the answer.
/// An empty <see cref="Expected"/> list means the assistant should abstain, because the answer is
/// in no document the asker may read.
/// </summary>
public sealed record SmokeQuestion(string Id, string Asker, string Kind, string Question, IReadOnlyList<string> Expected)
{
    public bool Answerable => Expected.Count > 0;
}

/// <summary>
/// The 25 questions Chapter 3 uses for the first baseline. They're a smoke test, not an
/// evaluation: Chapter 5 replaces them with a labeled golden set.
/// </summary>
public static class SmokeSet
{
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "corpus", "smoke-set.json");

    public static IReadOnlyList<SmokeQuestion> Load(string? path = null) =>
        JsonSerializer.Deserialize<List<SmokeQuestion>>(File.ReadAllText(path ?? FilePath), JsonSerializerOptions.Web)
        ?? throw new InvalidDataException("The smoke set is empty.");
}
