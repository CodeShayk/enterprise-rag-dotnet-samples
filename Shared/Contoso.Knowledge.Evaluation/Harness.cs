using System.Text.Json;
using Contoso.Corpus;

namespace Contoso.Knowledge.Evaluation;

/// <summary>What the harness needs from a retrieval configuration: all of its chunks and a ranked search.</summary>
public interface IRetrievalUnderTest
{
    string Name { get; }

    IReadOnlyDictionary<string, string> Parameters { get; }

    IReadOnlyList<ChunkSpan> Chunks { get; }

    Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default);
}

/// <summary>
/// One question's scores and the chunks it retrieved. Retrieval metrics are NaN for questions that
/// should end in an abstention.
/// </summary>
public sealed record QuestionScores(
    string Id, string Type, bool Answerable, int RestrictedChunks,
    double Hit, double Recall, double Precision, double ReciprocalRank, double AveragePrecision, double Ndcg,
    double ContextPrecision, double ContextRecall, double Sufficient, double Redundancy,
    IReadOnlyList<ChunkSpan> Retrieved);

/// <summary>A run: which configuration, which labels, which questions and every question's scores.</summary>
public sealed record RunRecord(
    string Configuration, IReadOnlyDictionary<string, string> Parameters, int K, string Split,
    string GoldenSetSha256, DateTimeOffset At, IReadOnlyList<QuestionScores> Questions)
{
    public static readonly string[] MetricNames =
        ["Hit", "Recall", "Precision", "MRR", "MAP", "nDCG", "CtxPrecision", "CtxRecall", "Sufficient", "Redundancy"];

    public IEnumerable<QuestionScores> Answerable => Questions.Where(q => q.Answerable);

    public static double Value(QuestionScores q, string metric) => metric switch
    {
        "Hit" => q.Hit,
        "Recall" => q.Recall,
        "Precision" => q.Precision,
        "MRR" => q.ReciprocalRank,
        "MAP" => q.AveragePrecision,
        "nDCG" => q.Ndcg,
        "CtxPrecision" => q.ContextPrecision,
        "CtxRecall" => q.ContextRecall,
        "Sufficient" => q.Sufficient,
        "Redundancy" => q.Redundancy,
        _ => throw new ArgumentException($"Unknown metric '{metric}'."),
    };

    public double Mean(string metric) => Answerable.Average(q => Value(q, metric));

    public async Task SaveAsync(string folder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{At:yyyyMMdd-HHmmss}-{Configuration}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, Indented), cancellationToken);
    }

    // Questions that should abstain have no retrieval scores; NaN is written as the string "NaN".
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerOptions.Web)
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}

/// <summary>Scores a retrieval configuration against the golden set.</summary>
public sealed class Harness(IReadOnlyList<SourceDocument> corpus, string goldenSetSha256)
{
    private readonly Dictionary<string, SourceDocument> _documents = corpus.ToDictionary(d => d.Id);

    public async Task<RunRecord> RunAsync(
        IRetrievalUnderTest system, IReadOnlyList<GoldenQuestion> questions, int k, string split, CancellationToken cancellationToken = default)
    {
        var scores = new List<QuestionScores>(questions.Count);
        foreach (var question in questions)
        {
            var asker = People.Find(question.Asker)!;
            var ranked = await system.RetrieveAsync(question.Question, asker, k, cancellationToken);
            var restricted = ranked.Count(chunk => !asker.CanRead(_documents[chunk.Doc]));

            if (!question.Answerable)
            {
                scores.Add(new QuestionScores(question.Id, question.Type, false, restricted,
                    double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                    ranked));
                continue;
            }

            var spans = GoldenSet.Locate(question, _documents);
            var grades = ranked.Select(chunk => Metrics.Grade(chunk, spans)).ToList();
            var corpusGrades = system.Chunks.Select(chunk => Metrics.Grade(chunk, spans)).Where(g => g > 0).ToList();
            var contextRecall = Metrics.ContextRecall(ranked, spans);

            scores.Add(new QuestionScores(question.Id, question.Type, true, restricted,
                Hit: Metrics.HitAt(grades, k),
                Recall: Metrics.RecallAt(ranked, spans),
                Precision: Metrics.PrecisionAt(grades, k),
                ReciprocalRank: Metrics.ReciprocalRank(grades, k),
                AveragePrecision: Metrics.AveragePrecisionAt(grades, k, corpusGrades.Count),
                Ndcg: Metrics.NdcgAt(grades, corpusGrades, k),
                ContextPrecision: Metrics.ContextPrecision(grades),
                ContextRecall: contextRecall,
                Sufficient: contextRecall == 1 ? 1 : 0,
                Redundancy: Metrics.Redundancy(ranked),
                Retrieved: ranked));
        }

        return new RunRecord(system.Name, system.Parameters, k, split, goldenSetSha256, DateTimeOffset.UtcNow, scores);
    }
}
