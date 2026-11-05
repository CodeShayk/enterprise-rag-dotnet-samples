using System.Diagnostics;
using System.Text.Json;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Ch05.Evaluation.Cli;

/// <summary>A drafted question, the quote the model offered as its answer, and whether that quote is really in the passage.</summary>
public sealed record Candidate(string Doc, string? Question, string? Quote, bool Verbatim, string? Raw);

/// <summary>The two commands that need a model: drafting synthetic questions and annotating relevance.</summary>
public static class ModelCommands
{
    /// <summary>Asks a local model for one question per document, with the sentence that answers it, for review.</summary>
    public static async Task GenerateAsync(EvaluationContext context, IConfiguration configuration)
    {
        using var chat = AIClientFactory.CreateChatClient(configuration);
        var chunks = Configurations.Chunk(context.Corpus, context.Tokenizer, 200, 40).GroupBy(c => c.Document.Id).Select(g => g.First()).ToList();
        var candidates = new List<Candidate>();
        var started = Stopwatch.StartNew();

        foreach (var chunk in chunks)
        {
            var response = await chat.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, "You write test questions for a search assistant used by the employees of an industrial manufacturer. Reply with JSON only."),
                    new ChatMessage(ChatRole.User,
                        $"Passage from \"{chunk.Document.Title}\":\n{chunk.Text}\n\n" +
                        "Write one question that an employee might ask and that this passage answers. Then copy, word for word, " +
                        "the sentence from the passage that answers it. Reply as JSON: {\"question\": \"...\", \"answer_quote\": \"...\"}"),
                ],
                new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 200, ResponseFormat = ChatResponseFormat.Json });

            string? question = null, quote = null;
            try
            {
                using var json = JsonDocument.Parse(response.Text);
                question = json.RootElement.GetProperty("question").GetString();
                quote = json.RootElement.GetProperty("answer_quote").GetString();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }

            var verbatim = !string.IsNullOrWhiteSpace(quote) && chunk.Text.Contains(quote.Trim(), StringComparison.Ordinal);
            candidates.Add(new Candidate(chunk.Document.Id, question, quote, verbatim, question is null ? response.Text : null));
            Console.WriteLine($"{chunk.Document.Id,-20} {(question is null ? "unparseable" : verbatim ? "quote found" : "quote NOT in passage"),-21} {question}");
        }

        var path = Path.Combine(context.RunsFolder, "synthetic-candidates.jsonl");
        Directory.CreateDirectory(context.RunsFolder);
        await File.WriteAllLinesAsync(path, candidates.Select(c => JsonSerializer.Serialize(c, JsonSerializerOptions.Web)));
        var parsed = candidates.Count(c => c.Question is not null);
        var found = candidates.Count(c => c.Verbatim);
        Console.WriteLine();
        Console.WriteLine($"{candidates.Count} passages, {parsed} parseable answers, {found} with the quote found verbatim; " +
                          $"{started.Elapsed.TotalMinutes:F1} min with {configuration["AI:OllamaModel"]}. Wrote {path} for review.");
    }

    /// <summary>
    /// Uses a local model as a second annotator: it judges each retrieved chunk for each question,
    /// and Cohen's kappa measures how far its judgments agree with the span labels.
    /// </summary>
    public static async Task AnnotateAsync(EvaluationContext context, IConfiguration configuration)
    {
        using var chat = AIClientFactory.CreateChatClient(configuration);
        var documents = context.Corpus.ToDictionary(d => d.Id);
        var retrieval = Configurations.Matrix(context.Corpus, context.Tokenizer).Single(c => c.Name == "keyword-100");
        var labels = new List<bool>();
        var judgments = new List<bool>();
        var started = Stopwatch.StartNew();

        foreach (var question in context.Questions.Where(q => q.Answerable))
        {
            var spans = GoldenSet.Locate(question, documents);
            foreach (var chunk in await retrieval.RetrieveAsync(question.Question, People.Find(question.Asker)!, context.K))
            {
                var response = await chat.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, "You judge whether a passage helps answer a question. Reply with yes or no only."),
                        new ChatMessage(ChatRole.User,
                            $"Question: {question.Question}\n\nPassage:\n{context.TextOf(chunk)}\n\n" +
                            "Does this passage contain information that answers the question, fully or in part?"),
                    ],
                    new ChatOptions { Temperature = 0f, MaxOutputTokens = 3 });

                labels.Add(Metrics.Grade(chunk, spans) > 0);
                judgments.Add(response.Text.TrimStart().StartsWith("yes", StringComparison.OrdinalIgnoreCase));
            }
        }

        var both = labels.Zip(judgments).Count(p => p.First && p.Second);
        var neither = labels.Zip(judgments).Count(p => !p.First && !p.Second);
        var onlyLabels = labels.Zip(judgments).Count(p => p.First && !p.Second);
        var onlyModel = labels.Zip(judgments).Count(p => !p.First && p.Second);
        Console.WriteLine($"{labels.Count} question-chunk pairs (split {context.Split}, keyword-100, top {context.K}), " +
                          $"{started.Elapsed.TotalMinutes:F1} min with {configuration["AI:OllamaModel"]}");
        Console.WriteLine();
        Console.WriteLine("                       model: relevant   model: not relevant");
        Console.WriteLine($"labels: relevant       {both,17} {onlyLabels,21}");
        Console.WriteLine($"labels: not relevant   {onlyModel,17} {neither,21}");
        Console.WriteLine();
        Console.WriteLine($"Observed agreement {(both + neither) / (double)labels.Count:P0}; Cohen's kappa {Agreement.CohensKappa(labels, judgments):F2}");
    }
}
