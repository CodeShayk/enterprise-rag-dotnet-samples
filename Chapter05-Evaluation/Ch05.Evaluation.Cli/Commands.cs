using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.ML.Tokenizers;

namespace Ch05.Evaluation.Cli;

/// <summary>Everything a command needs: the corpus, the labels and the run settings.</summary>
public sealed record EvaluationContext(
    IReadOnlyList<SourceDocument> Corpus, IReadOnlyList<GoldenQuestion> Golden, string GoldenSha256,
    Tokenizer Tokenizer, int K, string Split, string RunsFolder)
{
    public IReadOnlyList<GoldenQuestion> Questions => Split == "all" ? Golden : [.. Golden.Where(q => q.Split == Split)];

    public string TextOf(ChunkSpan chunk) => Corpus.First(d => d.Id == chunk.Doc).Text[chunk.Start..chunk.End];

    public static EvaluationContext Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("Evaluation");
        return new EvaluationContext(
            MarkdownCorpus.Load(), GoldenSet.Load(), GoldenSet.Sha256(),
            TiktokenTokenizer.CreateForEncoding("o200k_base"),
            section.GetValue("K", 4), section["Split"] ?? "dev", section["RunsFolder"] ?? "runs");
    }
}

public static class Commands
{
    private static readonly string[] Shown = ["Hit", "Recall", "Precision", "MRR", "MAP", "nDCG", "CtxPrecision", "CtxRecall", "Sufficient", "Redundancy"];

    public static int Validate(EvaluationContext context)
    {
        var documents = context.Corpus.ToDictionary(d => d.Id);
        var errors = new List<string>();
        var spans = 0;
        foreach (var question in context.Golden)
        {
            try
            {
                spans += GoldenSet.Locate(question, documents).Count;
            }
            catch (InvalidDataException e)
            {
                errors.Add(e.Message);
            }
        }

        var answerable = context.Golden.Count(q => q.Answerable);
        Console.WriteLine($"Golden set:  {Path.GetFileName(GoldenSet.DefaultPath)}, sha256 {context.GoldenSha256[..12]}");
        Console.WriteLine($"Questions:   {context.Golden.Count} ({answerable} answerable, {context.Golden.Count - answerable} should abstain); " +
                          $"dev {context.Golden.Count(q => q.Split == "dev")}, test {context.Golden.Count(q => q.Split == "test")}");
        Console.WriteLine($"Spans:       {spans} located in {context.Corpus.Count} documents; {errors.Count} errors");
        foreach (var error in errors)
        {
            Console.WriteLine($"  ERROR {error}");
        }

        Console.WriteLine();
        Console.WriteLine("Type           dev  test   Source        Questions");
        var types = context.Golden.GroupBy(q => q.Type).OrderByDescending(g => g.Count()).ToList();
        var sources = context.Golden.GroupBy(q => q.Source).OrderByDescending(g => g.Count()).ToList();
        for (var i = 0; i < Math.Max(types.Count, sources.Count); i++)
        {
            var type = i < types.Count ? $"{types[i].Key,-13} {types[i].Count(q => q.Split == "dev"),4} {types[i].Count(q => q.Split == "test"),5}" : new string(' ', 24);
            var source = i < sources.Count ? $"{sources[i].Key,-13} {sources[i].Count(),9}" : "";
            Console.WriteLine($"{type}   {source}");
        }

        Directory.CreateDirectory(context.RunsFolder);
        var qrels = Path.Combine(context.RunsFolder, "golden-set.v1.qrels");
        File.WriteAllLines(qrels, GoldenSet.ToTrecQrels(context.Golden));
        Console.WriteLine();
        Console.WriteLine($"Wrote {File.ReadAllLines(qrels).Length} TREC qrels lines to {qrels}");
        return errors.Count == 0 ? 0 : 1;
    }

    public static async Task<IReadOnlyList<RunRecord>> RunAsync(EvaluationContext context, bool print = true)
    {
        var harness = new Harness(context.Corpus, context.GoldenSha256);
        var runs = new List<RunRecord>();
        foreach (var configuration in Configurations.Matrix(context.Corpus, context.Tokenizer))
        {
            var run = await harness.RunAsync(configuration, context.Questions, context.K, context.Split);
            await run.SaveAsync(context.RunsFolder);
            runs.Add(run);
        }

        if (print)
        {
            var questions = runs[0].Questions;
            Console.WriteLine($"Split {context.Split}: {questions.Count} questions, {questions.Count(q => q.Answerable)} answerable; " +
                              $"k = {context.K}; golden set sha256 {context.GoldenSha256[..12]}");
            Console.WriteLine();
            Console.WriteLine("Configuration   Hit  Recall    P@k    MRR    MAP   nDCG |  CtxP   CtxR   Suff  Redund | Tokens  Leaks");
            foreach (var run in runs)
            {
                var m = Shown.Select(run.Mean).ToArray();
                var tokens = run.Answerable.Average(q => q.Retrieved.Sum(chunk => context.Tokenizer.CountTokens(context.TextOf(chunk))));
                Console.WriteLine($"{run.Configuration,-13} {m[0],5:F2} {m[1],7:F2} {m[2],6:F2} {m[3],6:F2} {m[4],6:F2} {m[5],6:F2} | " +
                                  $"{m[6],5:F2} {m[7],6:F2} {m[8],6:F2} {m[9],7:F2} | {tokens,6:F0} {run.Questions.Sum(q => q.RestrictedChunks),6}");
            }

            Console.WriteLine();
            Console.WriteLine($"Recall@{context.K} by question type");
            Console.WriteLine("Type            n  " + string.Join("", runs.Select(r => $"{r.Configuration,13}")));
            foreach (var type in runs[0].Answerable.GroupBy(q => q.Type).OrderBy(g => g.Key))
            {
                var ids = type.Select(q => q.Id).ToHashSet();
                Console.WriteLine($"{type.Key,-13} {type.Count(),3}  " + string.Join("", runs.Select(r =>
                    $"{r.Answerable.Where(q => ids.Contains(q.Id)).Average(q => q.Recall),13:F2}")));
            }

            // Error analysis starts with the questions the baseline didn't fully answer.
            Console.WriteLine();
            Console.WriteLine($"Not sufficient with {runs[0].Configuration}:");
            foreach (var q in runs[0].Answerable.Where(q => q.Sufficient < 1))
            {
                var question = context.Golden.Single(g => g.Id == q.Id);
                Console.WriteLine($"  {q.Id} {q.Type,-11} recall {q.Recall:F2}  {question.Question}");
                Console.WriteLine($"       retrieved {string.Join(", ", q.Retrieved.Select(c => $"{c.Doc}@{c.Start}"))}");
            }

            Console.WriteLine();
            Console.WriteLine($"Saved {runs.Count} runs to {context.RunsFolder}");
        }

        return runs;
    }

    public static async Task CompareAsync(EvaluationContext context, string baselineName, string candidateName)
    {
        var runs = await RunAsync(context, print: false);
        var baseline = runs.Single(r => r.Configuration == baselineName);
        var candidate = runs.Single(r => r.Configuration == candidateName);
        var a = baseline.Answerable.ToList();
        var b = candidate.Answerable.ToList();

        Console.WriteLine($"{candidateName} against {baselineName}, split {context.Split}: {a.Count} answerable questions, " +
                          "paired bootstrap, 10,000 resamples");
        Console.WriteLine();
        Console.WriteLine("Metric        Baseline  Candidate  Difference   95% interval");
        foreach (var metric in new[] { "Hit", "Recall", "nDCG", "CtxRecall", "Sufficient" })
        {
            var x = a.Select(q => RunRecord.Value(q, metric)).ToList();
            var y = b.Select(q => RunRecord.Value(q, metric)).ToList();
            var d = Bootstrap.Paired(x, y);
            Console.WriteLine($"{metric,-12} {x.Average(),9:F2} {y.Average(),10:F2} {d.Mean,+11:+0.00;-0.00} " +
                              $"  [{d.Low:+0.00;-0.00}, {d.High:+0.00;-0.00}]{(d.Clear ? "  clear" : "")}");
        }

        var discordant = a.Zip(b).Count(p => p.First.Sufficient != p.Second.Sufficient);
        Console.WriteLine();
        Console.WriteLine($"The configurations disagree on sufficiency for {discordant} of {a.Count} questions ({discordant / (double)a.Count:P0}).");
    }

    public static void PrintPower(double discordant)
    {
        Console.WriteLine($"Questions needed for a paired comparison (5% significance, 80% power) when configurations disagree on {discordant:P0} of questions:");
        foreach (var delta in new[] { 0.05, 0.10, 0.15, 0.20 })
        {
            Console.WriteLine(delta < discordant
                ? $"  to detect a difference of {delta,4:F2}: {Power.PairedQuestions(discordant, delta),5} answerable questions"
                : $"  to detect a difference of {delta,4:F2}: impossible; a difference can't exceed the disagreement rate");
        }
    }
}
