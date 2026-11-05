using System.Diagnostics;
using System.Text;
using Ch03.Knowledge;
using Contoso.Corpus;
using Microsoft.Extensions.AI;

namespace Ch03.Baseline.Cli;

/// <summary>One smoke question's result: where the expected source ranked, how long each stage took and the tokens.</summary>
public sealed record SmokeResult(
    SmokeQuestion Question,
    int? FirstRelevantRank,
    bool Abstained,
    int RestrictedChunks,
    TimeSpan Retrieval,
    ChatMeasurement Generation,
    string Answer)
{
    public bool Hit => FirstRelevantRank is not null;
}

/// <summary>Runs the smoke set through the assistant, one question at a time.</summary>
public sealed class SmokeRunner(KnowledgeAssistant assistant, ChatMeasurements measurements, IReadOnlyList<SourceDocument> corpus)
{
    public async Task<IReadOnlyList<SmokeResult>> RunAsync(IReadOnlyList<SmokeQuestion> questions, CancellationToken cancellationToken)
    {
        var results = new List<SmokeResult>(questions.Count);
        foreach (var question in questions)
        {
            var asker = People.Find(question.Asker) ?? throw new InvalidDataException($"Unknown asker '{question.Asker}'.");

            // Retrieval: embed the question, search with the permission filter, build the prompt.
            var started = Stopwatch.GetTimestamp();
            var answer = await assistant.AskAsync(question.Question, asker, cancellationToken);
            var retrieval = Stopwatch.GetElapsedTime(started);

            // Generation: the measuring middleware times the stream and counts its tokens.
            var text = new StringBuilder();
            await foreach (var update in answer.Updates.WithCancellation(cancellationToken))
            {
                text.Append(update.Text);
            }

            var rank = answer.Sources
                .Select((source, i) => (source, rank: i + 1))
                .FirstOrDefault(s => question.Expected.Contains(s.source.DocumentId)).rank;

            results.Add(new SmokeResult(
                question,
                FirstRelevantRank: rank == 0 ? null : rank,
                Abstained: IsAbstention(text.ToString()),
                RestrictedChunks: answer.Sources.Count(s => !asker.CanRead(corpus.Single(d => d.Id == s.DocumentId))),
                Retrieval: retrieval,
                Generation: measurements.Last ?? new ChatMeasurement(TimeSpan.Zero, TimeSpan.Zero, 0, 0, false),
                Answer: text.ToString()));
        }

        return results;
    }

    // An answer that opens with a refusal. Good enough for a smoke test; Chapter 14 makes
    // abstention a typed field of the answer instead of a phrase to look for.
    private static bool IsAbstention(string answer) =>
        new[] { "I don't know", "I do not know", "I couldn't find", "I can't answer", "The sources don't", "The sources do not" }
            .Any(phrase => answer.TrimStart().StartsWith(phrase, StringComparison.OrdinalIgnoreCase));
}
