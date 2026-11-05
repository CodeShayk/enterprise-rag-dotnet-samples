using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;

namespace Ch13.Query.Cli;

public static partial class Commands
{
    /// <summary>Spelling: the dev questions with one typo each, searched as typed and after correction against the index.</summary>
    public static async Task NormalizeAsync(Lab lab)
    {
        using var bge = Lab.Bge();
        var (hybrid, _, _) = await lab.HybridAsync(bge);
        var indexOnly = new SpellingCorrector(lab.Chunks.Select(c => c.Chunk.IndexText));
        var spelling = new SpellingCorrector(lab.Chunks.Select(c => c.Chunk.IndexText), Lab.KnownWords());
        var typos = lab.Answerable.Select(q => q with { Question = Typos.Apply(q.Question, q.Id) }).ToList();
        var indexOnlyClean = lab.Answerable.ToDictionary(q => q.Question, q => (indexOnly.Correct(q.Question).Text, (RetrievalFilter?)null));
        var corrected = typos.ToDictionary(q => q.Question, q => (spelling.Correct(q.Question).Text, (RetrievalFilter?)null));
        var cleanCorrected = lab.Answerable.ToDictionary(q => q.Question, q => (spelling.Correct(q.Question).Text, (RetrievalFilter?)null));

        var runs = new[]
        {
            ("as written", await lab.RunAsync(hybrid, lab.Answerable)),
            ("one typo each", await lab.RunAsync(hybrid, typos)),
            ("one typo, corrected", await lab.RunAsync(hybrid, typos, corrected)),
            ("as written, corrected", await lab.RunAsync(hybrid, lab.Answerable, cleanCorrected)),
            ("as written, index words only", await lab.RunAsync(hybrid, lab.Answerable, indexOnlyClean)),
        };
        var fixedTypos = typos.Zip(lab.Answerable).Count(p => spelling.Correct(p.First.Question).Text == p.Second.Question);
        var changedClean = lab.Answerable.Count(q => spelling.Correct(q.Question).Changes.Count > 0);
        Console.WriteLine($"Golden set v2, {lab.Split}: {lab.Answerable.Count} answerable questions; hybrid retrieval (Chapter 11); vocabulary of {spelling.Vocabulary:N0} words from the index");
        foreach (var (q, t) in lab.Answerable.Zip(typos).Take(3)) Console.WriteLine($"  {q.Question}  →  {t.Question}");
        Console.WriteLine();
        Console.WriteLine($"{"Questions",-24} {"Recall@4",8} {"nDCG@4",7} {"Suff.",6}   Sufficient against as written");
        foreach (var (name, run) in runs)
        {
            Console.WriteLine($"{name,-24} {run.Mean("Recall"),8:F2} {run.Mean("nDCG"),7:F2} {run.Mean("Sufficient"),6:F2}   " +
                (run == runs[0].Item2 ? "" : Lab.Interval(Lab.Compare(runs[0].Item2, run))));
        }

        Console.WriteLine($"Typos restored exactly: {fixedTypos} of {typos.Count}; correctly spelled questions changed: {changedClean} of {lab.Answerable.Count}, " +
            $"{lab.Answerable.Count(q => indexOnly.Correct(q.Question).Changes.Count > 0)} with the index's words only");
        foreach (var q in lab.Answerable.Where(q => indexOnly.Correct(q.Question).Changes.Count > 0).Take(4))
        {
            Console.WriteLine($"  index words only, {q.Id}: {string.Join(", ", indexOnly.Correct(q.Question).Changes.Select(c => $"{c.From} → {c.To}"))}");
        }
    }

    /// <summary>Intent checks: the nearest-example classifier on held-out examples and on the golden set, with and without the LLM.</summary>
    public static async Task IntentAsync(Lab lab)
    {
        using var bge = Lab.Bge();
        var embedder = new OnnxEmbeddingGenerator(bge);
        var examples = Lab.IntentExamples(paired: false);
        var pairedExamples = Lab.IntentExamples(paired: true);
        var test = Lab.Examples("intents.json", "test", Enum.Parse<Intent>).ToList();
        // Golden questions: answerable ones are in scope; of the nine that end in "I don't know", two are out of scope.
        var golden = lab.Questions.Select(q => (Text: q.Question, Label: q.Id is "G008" or "G064" ? Intent.OutOfScope : Intent.InScope)).ToList();
        var cache = Lab.Cache();
        var chat = Lab.Chat();
        Task<Intent> Llm(string question, CancellationToken ct) => LlmIntent(chat, cache, question, ct);

        Console.WriteLine($"{examples.Count} written examples; held-out set of {test.Count}; golden set v2 ({golden.Count} questions, {lab.Split})");
        Console.WriteLine();
        Console.WriteLine($"{"Classifier",-34} {"Held out",8} {"Golden",8} {"LLM calls",9} {"ms each",8}");
        foreach (var (name, set, margin, fallback) in new[]
        {
            ("nearest example", examples, 0.0, false),
            ("nearest example, paired examples", pairedExamples, 0.0, false),
            ("paired, LLM below a 0.03 margin", pairedExamples, 0.03, true),
            ("paired, LLM confirms each decline", pairedExamples, -1.0, true),
            ("LLM only", pairedExamples, 2.0, true),
        })
        {
            var classifier = await IntentClassifier.CreateAsync(embedder, set, fallback ? Llm : null, Math.Max(0, margin), confirmDeclines: margin < 0);
            var (calls, stopwatch) = (cache.Calls, Stopwatch.StartNew());
            var llmMs = cache.Milliseconds;
            int Correct(List<(string Text, Intent Label)> set, List<IntentResult> results) => set.Zip(results).Count(p => p.First.Label == p.Second.Intent);
            var held = new List<IntentResult>();
            foreach (var (text, _) in test) held.Add(await classifier.ClassifyAsync(text));
            var gold = new List<IntentResult>();
            foreach (var (text, _) in golden) gold.Add(await classifier.ClassifyAsync(text));
            var n = test.Count + golden.Count;
            // Cached LLM answers report the time their first call took.
            var ms = (stopwatch.Elapsed.TotalMilliseconds + (cache.Milliseconds - llmMs)) / n;
            Console.WriteLine($"{name,-34} {$"{Correct(test, held)}/{test.Count}",8} {$"{Correct(golden, gold)}/{golden.Count}",8} {cache.Calls - calls,9} {ms,8:F1}");
            if (name.StartsWith("nearest example", StringComparison.Ordinal))
            {
                foreach (var (item, result) in test.Concat(golden).Zip(held.Concat(gold)).Where(p => p.First.Label != p.Second.Intent).Take(8))
                {
                    Console.WriteLine($"    {item.Label,-10} → {result.Intent,-10} {Trim(item.Text, 64)}");
                }
            }
        }
    }

    /// <summary>How many requests that must be declined the kept intent check declines: the held-out unsafe examples, and a list written for this test.</summary>
    public static async Task UnsafeAsync()
    {
        using var bge = Lab.Bge();
        var cache = Lab.Cache();
        var chat = Lab.Chat();
        var classifier = await IntentClassifier.CreateAsync(new OnnxEmbeddingGenerator(bge), Lab.IntentExamples(paired: true), (q, ct) => LlmIntent(chat, cache, q, ct), 0, confirmDeclines: true);
        var heldOut = Lab.Examples("intents.json", "test", Enum.Parse<Intent>).Where(e => e.Label == Intent.Unsafe).Select(e => e.Text).ToList();
        var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "unsafe-requests.v1.json")))!;
        Console.WriteLine("Must be declined; the kept check (paired examples, the LLM confirms each decline)");
        foreach (var (name, requests) in new[] { ("held-out unsafe examples", heldOut), ("unsafe-requests.v1.json", list) })
        {
            var through = new List<(string Text, Intent Intent)>();
            foreach (var request in requests)
            {
                var result = await classifier.ClassifyAsync(request);
                if (result.Intent != Intent.Unsafe) through.Add((request, result.Intent));
            }

            Console.WriteLine($"{name,-26} {requests.Count - through.Count} of {requests.Count} declined");
            foreach (var (text, intent) in through) Console.WriteLine($"    {intent,-10} {Trim(text, 88)}");
        }
    }

    /// <summary>Follow-ups: the second half of each two-part golden question asked after its first half.</summary>
    public static async Task CondenseAsync(Lab lab)
    {
        using var bge = Lab.Bge();
        var (hybrid, _, _) = await lab.HybridAsync(bge);
        var cache = Lab.Cache();
        var condenser = new Condenser(Lab.Chat(), cache);
        var conversations = Conversations.From(lab.Answerable);
        var followUps = conversations.Select(c => c.Original with { Question = c.FollowUp }).ToList();
        var (calls, ms) = (cache.Calls, cache.Milliseconds);
        var condensed = new Dictionary<string, (string, RetrievalFilter?)>();
        foreach (var c in conversations) condensed[c.FollowUp] = (await condenser.CondenseAsync([c.First], c.FollowUp), null);
        var latency = (cache.Milliseconds - ms) / Math.Max(1, cache.Calls - calls);
        var joined = conversations.ToDictionary(c => c.FollowUp, c => ($"{c.First} {c.FollowUp}", (RetrievalFilter?)null));

        Console.WriteLine($"Golden set v2, {lab.Split}: {conversations.Count} two-part questions asked as a question and a follow-up; scored against the whole question");
        foreach (var c in conversations.Take(2)) Console.WriteLine($"  \"{c.First}\" + \"{c.FollowUp}\" → \"{condensed[c.FollowUp].Item1}\"");
        Console.WriteLine();
        Console.WriteLine($"{"Searched with",-30} {"Recall@4",8} {"Suff.",6}   Sufficient against the follow-up alone");
        var baseline = await lab.RunAsync(hybrid, followUps);
        foreach (var (name, run) in new[]
        {
            ("the follow-up alone", baseline),
            ("previous turn + follow-up", await lab.RunAsync(hybrid, followUps, joined)),
            ("condensed by llama3.2", await lab.RunAsync(hybrid, followUps, condensed)),
            ("the whole question", await lab.RunAsync(hybrid, conversations.Select(c => c.Original).ToList())),
        })
        {
            Console.WriteLine($"{name,-30} {run.Mean("Recall"),8:F2} {run.Mean("Sufficient"),6:F2}   " + (run == baseline ? "" : Lab.Interval(Lab.Compare(baseline, run))));
        }

        Console.WriteLine($"Condensation: {latency:N0} ms a follow-up on the CPU");
        if (lab.Split != "dev") return;

        // Conversations that change topic: joined, the second turn carries the first one's words into the search.
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topic-changes.v1.json")));
        var turns = document.RootElement.EnumerateArray().Select(t => (First: t.GetProperty("first").GetString()!, Second: lab.Answerable.Single(q => q.Id == t.GetProperty("second").GetString()))).ToList();
        var seconds = turns.Select(t => t.Second with { Question = "Thanks. " + t.Second.Question }).ToList();
        var joinedTurns = turns.ToDictionary(t => "Thanks. " + t.Second.Question, t => ($"{t.First} Thanks. {t.Second.Question}", (RetrievalFilter?)null));
        var condensedTurns = new Dictionary<string, (string, RetrievalFilter?)>();
        foreach (var t in turns) condensedTurns["Thanks. " + t.Second.Question] = (await condenser.CondenseAsync([t.First], "Thanks. " + t.Second.Question), null);

        Console.WriteLine();
        Console.WriteLine($"{turns.Count} conversations whose second turn changes topic; scored against the second question");
        Console.WriteLine($"{"Searched with",-30} {"Recall@4",8} {"Suff.",6}   Sufficient against the second turn alone");
        var alone = await lab.RunAsync(hybrid, seconds);
        foreach (var (name, run) in new[]
        {
            ("the second turn alone", alone),
            ("previous turn + second turn", await lab.RunAsync(hybrid, seconds, joinedTurns)),
            ("condensed by llama3.2", await lab.RunAsync(hybrid, seconds, condensedTurns)),
        })
        {
            Console.WriteLine($"{name,-30} {run.Mean("Recall"),8:F2} {run.Mean("Sufficient"),6:F2}   " + (run == alone ? "" : Lab.Interval(Lab.Compare(alone, run))));
        }
    }

    /// <summary>Rewriting, multi-query fusion, decomposition and HyDE against the hybrid, with their LLM cost.</summary>
    public static async Task TransformAsync(Lab lab)
    {
        using var bge = Lab.Bge();
        var (hybrid, keyword, _) = await lab.HybridAsync(bge);
        var passageSide = await DenseRetriever.CreateAsync("dense, passage side", lab.Chunks, new OnnxEmbeddingGenerator(bge), new OnnxEmbeddingGenerator(bge));
        var cache = Lab.Cache();
        var rewriter = new Rewriter(Lab.Chat(), cache, new OnnxEmbeddingGenerator(bge));
        var rrf = new ReciprocalRankFusion(60);
        var twoPart = lab.Answerable.Where(q => Conversations.IsTwoPart(q.Question)).ToList();

        var configurations = new (string Name, IRetriever Retriever)[]
        {
            ("hybrid (Chapter 11)", hybrid),
            ("rewritten, drift guard 0.8", new MultiQueryRetriever("rewrite", hybrid, async (q, ct) => [await rewriter.RewriteAsync(q, ct)], rrf)),
            ("multi-query, 3 variants + RRF", new MultiQueryRetriever("multi-query", hybrid, (q, ct) => rewriter.VariantsAsync(q, 3, ct), rrf)),
            ("decomposed + RRF", new MultiQueryRetriever("decompose", hybrid, (q, ct) => rewriter.DecomposeAsync(q, ct), rrf)),
            ("decomposed when compound", new MultiQueryRetriever("decompose compound", hybrid,
                async (q, ct) => Compound.IsCompound(q) ? await rewriter.DecomposeAsync(q, ct) : [q], rrf)),
            ("HyDE: keyword + dense on passage", new HydeRetriever("hyde", keyword, passageSide, rewriter, rrf)),
        };
        Console.WriteLine($"Golden set v2, {lab.Split}: {lab.Answerable.Count} answerable questions ({twoPart.Count} of them in two parts); llama3.2 on the CPU");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-34} {"Suff.",6} {"nDCG@4",6} {"Two-part",8} {"LLM calls",9} {"s a question",12}   Sufficient against the hybrid");
        RunRecord? baseline = null;
        foreach (var (name, retriever) in configurations)
        {
            var (calls, ms) = (cache.Calls, cache.Milliseconds);
            var run = await lab.RunAsync(retriever);
            var perQuestion = (cache.Milliseconds - ms) / 1000 / lab.Answerable.Count;
            var callCount = cache.Calls - calls;
            var parts = run.Answerable.Where(q => twoPart.Any(t => t.Id == q.Id)).Average(q => q.Sufficient);
            baseline ??= run;
            Console.WriteLine($"{name,-34} {run.Mean("Sufficient"),6:F2} {run.Mean("nDCG"),6:F2} {parts,8:F2} {callCount,9} {perQuestion,12:F2}   " +
                (run == baseline ? "" : Lab.Interval(Lab.Compare(baseline, run))));
        }

        Console.WriteLine($"Rewrites rejected by the drift guard: {rewriter.Rejected}");
        foreach (var q in lab.Answerable.Where(q => q.Id is "G002" or "G041" or "G2-T007"))
        {
            Console.WriteLine($"  {q.Id} \"{q.Question}\"");
            Console.WriteLine($"    rewrite: {await rewriter.RewriteAsync(q.Question)}");
            Console.WriteLine($"    HyDE: {Trim((await rewriter.HypotheticalAsync(q.Question)).ReplaceLineEndings(" "), 150)}");
        }
    }

    /// <summary>Filters read from the question: rules, strict and expanded, and the LLM, against no filters.</summary>
    public static async Task FiltersAsync(Lab lab, bool llm)
    {
        using var bge = Lab.Bge();
        var (hybrid, _, _) = await lab.HybridAsync(bge);
        var rules = RuleFilterExtractor.FromAssetRecords(lab.Chunks);
        var cache = Lab.Cache();
        var rewriter = new Rewriter(Lab.Chat(), cache, new OnnxEmbeddingGenerator(bge));
        Dictionary<string, (string, RetrievalFilter?)> Plans(Func<GoldenQuestion, RetrievalFilter?> filter) =>
            lab.Answerable.ToDictionary(q => q.Question, q => (q.Question, filter(q)));

        var variants = new List<(string Name, Dictionary<string, (string, RetrievalFilter?)> Plans)>
        {
            ("no filters", Plans(_ => null)),
            ("rules: tags as written", Plans(q => rules.Extract(q.Question, expand: false, includeUntagged: false))),
            ("rules: + the asset's tags", Plans(q => rules.Extract(q.Question, expand: true, includeUntagged: false))),
            ("rules: + untagged documents", Plans(q => rules.Extract(q.Question))),
            ("rules: document types only", Plans(q => rules.Extract(q.Question) is { DocumentTypes: { } types } ? new RetrievalFilter { DocumentTypes = types } : null)),
        };
        var unparsed = new List<int>();
        if (llm)
        {
            foreach (var (name, schema) in new[] { ("llama3.2: + asset's tags, untagged", false), ("llama3.2, JSON schema: the same", true) })
            {
                var failures = rewriter.FilterParseFailures;
                var extracted = new Dictionary<string, (string, RetrievalFilter?)>();
                foreach (var q in lab.Answerable)
                {
                    var (equipment, types) = await rewriter.FiltersAsync(q.Question, schema);
                    var tags = equipment.SelectMany(t => rules.Extract(t)?.EquipmentTags ?? []).Distinct().ToList();
                    extracted[q.Question] = (q.Question, tags.Count == 0 && types.Count == 0 ? null
                        : new RetrievalFilter { EquipmentTags = tags.Count == 0 ? null : tags, DocumentTypes = types.Count == 0 ? null : types });
                }

                variants.Add((name, extracted));
                unparsed.Add(rewriter.FilterParseFailures - failures);
            }
        }

        Console.WriteLine($"Golden set v2, {lab.Split}: {lab.Answerable.Count} answerable questions; hybrid retrieval; filters applied inside both searches");
        Console.WriteLine();
        Console.WriteLine($"{"Filters",-36} {"Filtered",8} {"Suff.",6} {"nDCG@4",6}   Sufficient against no filters");
        RunRecord? baseline = null;
        foreach (var (name, plans) in variants)
        {
            var run = await lab.RunAsync(hybrid, lab.Answerable, plans);
            baseline ??= run;
            Console.WriteLine($"{name,-36} {plans.Values.Count(p => p.Item2 is not null),8} {run.Mean("Sufficient"),6:F2} {run.Mean("nDCG"),6:F2}   " +
                (run == baseline ? "" : Lab.Interval(Lab.Compare(baseline, run))));
        }

        if (llm) Console.WriteLine($"Replies that didn't parse: {unparsed[0]} of {lab.Answerable.Count} with the form in the prompt, {unparsed[1]} with the schema");
        foreach (var q in lab.Answerable.Where(q => q.Id is "G001" or "G046").Concat(lab.Answerable.Where(q => rules.Extract(q.Question)?.DocumentTypes is not null).Take(1)))
        {
            var f = rules.Extract(q.Question);
            Console.WriteLine($"  {q.Id} \"{Trim(q.Question, 56)}\": equipment {string.Join(", ", f?.EquipmentTags ?? [])}; types {string.Join(", ", f?.DocumentTypes ?? [])}");
        }
    }

    /// <summary>The semantic router on held-out examples and on the golden set.</summary>
    public static async Task RouteAsync(Lab lab)
    {
        using var bge = Lab.Bge();
        var test = Lab.Examples("routes.json", "test", r => r);
        foreach (var margin in new[] { 0.0, 0.05, 0.10 })
        {
            var router = await SemanticRouter.CreateAsync(new OnnxEmbeddingGenerator(bge), Lab.Examples("routes.json", "examples", r => r), "retrieve", margin);
            await RouteWithAsync(lab, router, test, margin);
        }
    }

    private static async Task RouteWithAsync(Lab lab, SemanticRouter router, IReadOnlyList<(string Text, string Label)> test, double margin)
    {
        Console.WriteLine($"Margin {margin:0.00} over the default route, retrieve:");
        var stopwatch = Stopwatch.StartNew();
        var routed = new List<(string Text, string Label, string Route)>();
        foreach (var (text, label) in test) routed.Add((text, label, await router.RouteAsync(text)));
        var golden = new List<string>();
        foreach (var q in lab.Questions) golden.Add(await router.RouteAsync(q.Question));
        var ms = stopwatch.Elapsed.TotalMilliseconds / (test.Count + lab.Questions.Count);
        Console.WriteLine($"Held-out examples: {routed.Count(r => r.Label == r.Route)} of {routed.Count} routed as labeled; {ms:F1} ms a question");
        foreach (var r in routed.Where(r => r.Label != r.Route)) Console.WriteLine($"  {r.Label,-9} → {r.Route,-9} {r.Text}");
        Console.WriteLine($"Golden set v2, {lab.Split} ({lab.Questions.Count}): " + string.Join(", ", golden.GroupBy(g => g).Select(g => $"{g.Key} {g.Count()}")));
        foreach (var (q, route) in lab.Questions.Zip(golden).Where(p => p.Second != "retrieve").Take(margin == 0 ? 3 : 6)) Console.WriteLine($"  {route,-9} {q.Id} {Trim(q.Question, 70)}");
    }

    /// <summary>The planner's steps, first all of them as first built, then each fixed step alone, then the steps kept.</summary>
    public static async Task EvaluateAsync(Lab lab)
    {
        using var bge = Lab.Bge();
        var (hybrid, _, _) = await lab.HybridAsync(bge);
        var embedder = new OnnxEmbeddingGenerator(bge);
        var cache = Lab.Cache();
        var chat = Lab.Chat();
        var texts = lab.Chunks.Select(c => c.Chunk.IndexText).ToList();
        var routes = Lab.Examples("routes.json", "examples", r => r);
        var decomposer = new Rewriter(chat, cache, embedder);
        var planners = new (string Name, QueryPlanner Planner)[]
        {
            ("every step, first versions", new QueryPlanner(
                await IntentClassifier.CreateAsync(embedder, Lab.IntentExamples(paired: false)), new SpellingCorrector(texts),
                RuleFilterExtractor.FromAssetRecords(lab.Chunks), null, await SemanticRouter.CreateAsync(embedder, routes))),
            ("intent: paired, LLM confirms declines", new QueryPlanner(
                await IntentClassifier.CreateAsync(embedder, Lab.IntentExamples(paired: true), (q, ct) => LlmIntent(chat, cache, q, ct), 0, confirmDeclines: true),
                null, null, null, null)),
            ("decompose compound questions", new QueryPlanner(null, null, null, null, null, decomposer)),
            ("kept: unsafe, chit-chat out; split", new QueryPlanner(
                await IntentClassifier.CreateAsync(embedder, Lab.IntentExamples(paired: true), (q, ct) => LlmIntent(chat, cache, q, ct), 0, confirmDeclines: true),
                null, null, null, await SemanticRouter.CreateAsync(embedder, routes, "retrieve", 0.10), decomposer, declineOutOfScope: false)),
        };

        var baseline = await lab.RunAsync(hybrid);
        Console.WriteLine($"Golden set v2, {lab.Split}: {lab.Questions.Count} questions, {lab.Answerable.Count} answerable; hybrid retrieval (Chapter 11) after each planner");
        Console.WriteLine();
        Console.WriteLine($"{"Planner",-38} {"Suff.",6} {"Declined",8} {"p50 ms",7} {"p95 ms",7}   Sufficient against no planner");
        Console.WriteLine($"{"none (Chapter 11)",-38} {baseline.Mean("Sufficient"),6:F2} {"0 / 0",8} {0,7:F1} {0,7:F1}");
        RunRecord? kept = null;
        List<(string Id, Intent Intent)> declinedLast = [];
        foreach (var (name, planner) in planners)
        {
            var (plans, times) = (new Dictionary<string, QueryPlan>(), new List<double>());
            foreach (var q in lab.Questions)
            {
                var llmBefore = cache.Milliseconds;
                var stopwatch = Stopwatch.StartNew();
                plans[q.Question] = await planner.PlanAsync(q.Question, []);
                // Cached LLM answers cost nothing now; count the time their first call took.
                times.Add(stopwatch.Elapsed.TotalMilliseconds + (cache.Milliseconds - llmBefore));
            }

            var run = await lab.Harness.RunAsync(new Configuration(new PlannedRetriever(hybrid, plans), lab.Chunks), lab.Questions, 4, lab.Split);
            kept = run;
            times.Sort();
            var declined = lab.Questions.Where(q => !plans[q.Question].Retrieve).ToList();
            declinedLast = [.. declined.Select(q => (q.Id, plans[q.Question].Intent))];
            Console.WriteLine($"{name,-38} {run.Mean("Sufficient"),6:F2} {$"{declined.Count(q => q.Answerable)} / {declined.Count(q => !q.Answerable)}",8} " +
                $"{times[times.Count / 2],7:F1} {times[(int)(times.Count * 0.95)],7:F1}   {Lab.Interval(Lab.Compare(baseline, run))}");
        }

        Console.WriteLine("Declined: answerable questions turned away / of those that should end in \"I don't know\"");
        Console.WriteLine($"Declined by the last planner: {string.Join("; ", declinedLast.Select(d => $"{d.Id} {d.Intent}"))}");
        var types = lab.Answerable.GroupBy(q => q.Type).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        Console.WriteLine();
        Console.WriteLine($"{"Sufficient by question type",-38} " + string.Join(" ", types.Select(t => $"{t,11}")));
        foreach (var (name, run) in new[] { ("none (Chapter 11)", baseline), ("kept steps", kept!) })
        {
            Console.WriteLine($"{name,-38} " + string.Join(" ", types.Select(t => $"{run.Answerable.Where(q => q.Type == t).Average(q => q.Sufficient),11:F2}")));
        }
    }

    internal static async Task<Intent> LlmIntent(Microsoft.Extensions.AI.IChatClient chat, LlmCache cache, string question, CancellationToken ct)
    {
        var prompt = $"""
            Classify this message to Contoso's maintenance knowledge assistant. Reply with one word:
            InScope (a question about Contoso's equipment, maintenance, procedures, parts, suppliers or policies),
            OutOfScope (anything else), ChitChat (greetings, thanks, small talk) or Unsafe (a request to defeat a safety
            function or to reach information the person may not see).

            Message: {question}
            """;
        var text = await cache.GetAsync(chat, "intent", prompt, 5, ct);
        return Enum.GetValues<Intent>().FirstOrDefault(i => text.Contains(i.ToString(), StringComparison.OrdinalIgnoreCase), Intent.InScope);
    }

    internal static string Trim(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>Deterministic typos: one word of five letters or more, not an identifier, with a letter dropped or two swapped.</summary>
public static partial class Typos
{
    public static string Apply(string question, string id)
    {
        var random = new Random(StableHash(id));
        var words = Word().Matches(question).Where(m => m.Length >= 5 && !Identifiers.IsIdentifier(m.Value)).ToList();
        if (words.Count == 0) return question;
        var word = words[random.Next(words.Count)];
        var i = 1 + random.Next(word.Length - 2);
        var typo = random.Next(2) == 0 ? word.Value.Remove(i, 1) : word.Value[..i] + word.Value[i + 1] + word.Value[i] + word.Value[(i + 2)..];
        return question[..word.Index] + typo + question[(word.Index + word.Length)..];
    }

    private static int StableHash(string text) => text.Aggregate(2027, (h, c) => unchecked(h * 31 + c));

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();
}

/// <summary>Two-part golden questions split into a question and a follow-up that leans on it.</summary>
public static partial class Conversations
{
    public sealed record Conversation(GoldenQuestion Original, string First, string FollowUp);

    public static bool IsTwoPart(string question) => TwoPart().IsMatch(question);

    public static IReadOnlyList<Conversation> From(IEnumerable<GoldenQuestion> questions) =>
        [.. questions.Select(q => (q, m: TwoPart().Match(q.Question))).Where(x => x.m.Success)
            .Select(x => new Conversation(x.q, x.m.Groups[1].Value + "?", "And " + char.ToLowerInvariant(x.m.Groups[2].Value[0]) + x.m.Groups[2].Value[1..] + "?"))
            .GroupBy(c => c.FollowUp).Select(g => g.First())];   // "And does it trip the pump?" once, not once per error code

    [GeneratedRegex(@"^((?:What|Which|How|Who|When|Why|Is|Does|Can)\b.+?), and (.+)\?$")]
    private static partial Regex TwoPart();
}
