using System.Text.Json;
using System.Text.RegularExpressions;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;

namespace Ch15.Evaluation.Cli;

/// <summary>A snapshot of per-question groundedness, the baseline a regression suite compares with.</summary>
public sealed record Snapshot(string Setup, string Split, string GoldenSetSha256, string Nli, double Threshold, string Created, IReadOnlyList<ScoredQuestion> Questions);

/// <summary>
/// How an answer becomes statements with citations, for the citation tests and the regression suite. The grounded
/// generator's claims are statements already, each citing one block. Chapter 3's free text is split into sentences;
/// each cites the [n] markers it contains, or those of the last marked sentence before it (of the first marked
/// sentence, before any marker), which is how a reader takes "According to [1], ..." followed by more sentences.
/// </summary>
public static partial class Grounding
{
    /// <summary>The premise an evaluator reads for a block: the section path the generator saw, then the text.</summary>
    public static string Premise(EvidenceBlock block) => $"{block.Section}\n{block.Text}";

    /// <summary>The statements of an answer that a user would read as facts; a declined answer has none.</summary>
    public static IReadOnlyList<Statement> Statements(Answer answer)
    {
        if (answer.Status is AnswerStatus.NotFound or AnswerStatus.Clarify) return [];
        var premises = answer.Evidence.ToDictionary(b => b.Id, Premise);
        if (answer.PromptVersion != "chapter3")
        {
            return [.. answer.Claims.Select(c => new Statement(c.Text, c.Source is { } s && premises.TryGetValue(s, out var p) ? [p] : []))];
        }

        var sentences = ClaimSupport.Split(answer.Text);
        var markers = sentences.Select(s => Marker().Matches(s).Select(m => m.Groups[1].Value).Distinct().ToList()).ToList();
        var carried = markers.FirstOrDefault(m => m.Count > 0) ?? [];
        var statements = new List<Statement>();
        for (var i = 0; i < sentences.Count; i++)
        {
            if (markers[i].Count > 0) carried = markers[i];
            var text = Spaces().Replace(Marker().Replace(LeadIn().Replace(sentences[i], ""), ""), " ").Trim();
            if (text.Length > 0) statements.Add(new Statement(text, [.. carried.Where(premises.ContainsKey).Select(n => premises[n])]));
        }

        return statements;
    }

    /// <summary>Citations of sources the evidence doesn't have: an S-number or [n] the model made up.</summary>
    public static int Fabricated(Answer answer) => answer.PromptVersion == "chapter3"
        ? Marker().Matches(answer.Text).Select(m => m.Groups[1].Value).Distinct().Count(n => answer.Evidence.All(b => b.Id != n))
        : answer.Claims.Count(c => c.Source is { Length: > 0 } && !c.SourceExists);

    /// <summary>
    /// The groundedness of every recorded answer of a split, question by question: the share of its statements that the
    /// sources they cite entail (ALCE's citation recall). Declined answers have no score.
    /// </summary>
    public static IReadOnlyList<ScoredQuestion> Score(IReadOnlyDictionary<string, Answer> answers, Func<string, string> slice, CitationScorer scorer) =>
        [.. answers.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => Statements(a.Value) is { Count: > 0 } statements
            ? new ScoredQuestion(a.Key, slice(a.Key), scorer.Score(statements).Recall)
            : new ScoredQuestion(a.Key, slice(a.Key), double.NaN))];

    public static Snapshot Read(string path) => JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Json)!;

    public static void Write(Snapshot snapshot, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, Json) + "\n");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web) { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    [GeneratedRegex(@"\s*\[(\d+)\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\b(?:[Aa]ccording to|[Aa]s stated in|[Ss]ee)\s+(?:source\s+)?\[\d+\](?:\s*(?:,|and)\s*\[\d+\])*,?\s*")]
    private static partial Regex LeadIn();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
