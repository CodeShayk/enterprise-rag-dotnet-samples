using System.Diagnostics;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;

namespace Ch15.Evaluation.Cli;

/// <summary>The experiments that need no language model: checks, NLI support, citation tests, regressions, errors.</summary>
public static class Commands
{
    /// <summary>
    /// The labeling worksheet: for each answer of the sample, the question's labeled facts, the answer and every claim
    /// with its quote and the text around the quote in the block it cites. The labels are made from this, not from the corpus.
    /// </summary>
    public static void Worksheet(Lab lab, int from, int to)
    {
        var answers = Lab.Answers("compressed", "dev");
        var sample = lab.Sample();
        for (var n = from; n < Math.Min(to, sample.Count); n++)
        {
            var (q, a) = (sample[n], answers[sample[n].Id]);
            Console.WriteLine($"### {n} {q.Id} [{q.Type}] {q.Question}");
            foreach (var fact in lab.References(q)) Console.WriteLine($"  FACT {Shorten(fact, 160)}");
            Console.WriteLine($"  STATUS {a.Status} | {Shorten(a.Text, 400)}");
            foreach (var (c, i) in a.Claims.Select((c, i) => (c, i)))
            {
                Console.WriteLine($"  CLAIM {i} [{c.Source ?? "-"}] found={c.QuoteFound} | {Shorten(c.Text, 200)}");
                if (a.Evidence.FirstOrDefault(b => b.Id == c.Source) is not { } block) continue;
                var at = c.Quote.Length == 0 ? -1 : block.Text.IndexOf(c.Quote[..Math.Min(40, c.Quote.Length)], StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"     quote: {Shorten(c.Quote, 200)}");
                Console.WriteLine($"     src:   {Shorten(at < 0 ? block.Text : block.Text[Math.Max(0, at - 80)..Math.Min(block.Text.Length, at + 260)], 340)}");
            }
        }
    }

    /// <summary>
    /// The deterministic checks over every recorded answer: citations that point nowhere, quotes that aren't in their
    /// source, a status that contradicts the claims, identifiers the evidence doesn't contain, personal data and
    /// instructions to defeat a safety function. Counts are of answers.
    /// </summary>
    public static void Checks(Lab lab)
    {
        var runs = new (string Name, string Setup, string Split)[] { ("Chapter 3, dev", "chapter3", "dev"), ("Shipped, dev", "compressed", "dev"), ("Shipped, test", "compressed", "test") };
        var checks = new (string Name, Func<GoldenQuestion, Answer, IReadOnlyList<string>?> Find)[]
        {
            ("A statement cites no source", (q, a) => Grounding.Statements(a).Any(s => s.Cited.Count == 0) ? ["uncited"] : []),
            ("A citation names no source", (q, a) => Grounding.Fabricated(a) > 0 ? ["fabricated"] : []),
            ("A quote isn't in its source", (q, a) => a.PromptVersion == "chapter3" ? null : a.Claims.Any(c => c.SourceExists && c.Quote.Length > 0 && !c.QuoteFound) ? ["quote"] : []),
            ("Declined, but a claim cites a source", (q, a) => a.Status is AnswerStatus.NotFound && a.Claims.Any(c => c.SourceExists) ? ["status"] : []),
            ("An identifier isn't in the evidence", (q, a) => [.. AnswerChecks.UnknownIdentifiers(a.Text, a.Evidence.Select(b => $"{b.DocumentId} {b.Section}\n{b.Text}"), q.Question).Select(f => f.Detail)]),
            ("Personal data", (q, a) => [.. AnswerChecks.PersonalData(a.Text).Select(f => f.Detail)]),
            ("Defeats a safety function", (q, a) => [.. AnswerChecks.Forbidden(a.Text).Select(f => f.Detail)]),
        };

        var results = new List<int?[]>();
        var examples = new List<string>();
        var (clock, evaluated) = (new Stopwatch(), 0);
        foreach (var run in runs)
        {
            var answers = Lab.Answers(run.Setup, run.Split);
            var counts = new int?[checks.Length];
            foreach (var (id, answer) in answers.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                var question = lab.Question(id);
                for (var c = 0; c < checks.Length; c++)
                {
                    clock.Start();
                    var found = checks[c].Find(question, answer);
                    clock.Stop();
                    if (found is null) continue;
                    counts[c] = (counts[c] ?? 0) + (found.Count > 0 ? 1 : 0);
                    if (found.Count > 0 && c >= 4) examples.Add($"{run.Name} {id}: {string.Join(", ", found)}");
                }

                evaluated++;
            }

            results.Add(counts);
        }

        Console.WriteLine($"{"Answers with",-38} {string.Join(" ", runs.Select(r => $"{r.Name,14}"))}");
        Console.WriteLine($"{"(all answers)",-38} {string.Join(" ", runs.Select(r => $"{Lab.Answers(r.Setup, r.Split).Count,14}"))}");
        for (var c = 0; c < checks.Length; c++)
        {
            Console.WriteLine($"{checks[c].Name,-38} {string.Join(" ", results.Select(r => $"{(r[c] is { } n ? n.ToString() : "n/a"),14}"))}");
        }

        Console.WriteLine();
        Console.WriteLine($"All checks: {clock.Elapsed.TotalMicroseconds / evaluated:0} µs an answer");
        foreach (var example in examples) Console.WriteLine($"  {example}");
    }

    /// <summary>
    /// NLI support against the labels: each claim against the block it cites, by two DeBERTa-v3 xsmall models
    /// and a base model (also 8-bit), with the whole block as the premise or the best of the block and its three-sentence
    /// windows. Quote matching, Chapter 14's validation, is the baseline. A threshold sweep follows for the detector the
    /// later experiments use.
    /// </summary>
    public static void Nli(Lab lab)
    {
        var answers = Lab.Answers("compressed", "dev");
        var items = Lab.ClaimLabels().Select(l => (Label: l, Claim: answers[l.Id].Claims[l.Claim], Block: answers[l.Id].Evidence.First(b => b.Id == l.Source))).ToList();
        var truth = items.Select(i => i.Label.Supported).ToList();
        Console.WriteLine($"{items.Count} claims in {items.Select(i => i.Label.Id).Distinct().Count()} shipped answers (dev sample), labeled: {truth.Count(t => t)} supported, {truth.Count(t => !t)} not");
        Console.WriteLine();
        double[] thresholds = [0.02, 0.05, 0.1, 0.2, 0.3, 0.5, 0.7, 0.9];
        Console.WriteLine($"{"Detector",-36} {"At P >= 0.5: kappa",18} {"caught",7} {"false alarms",13} {"AUC",5} {"Tuned kappa",14} {"ms/claim",9}");
        var rows = new List<(string Key, double[] Scores)>();
        void Row(string key, string name, double[] scores, double ms)
        {
            rows.Add((key, scores));
            var predicted = scores.Select(s => s >= 0.5).ToList();
            var tuned = thresholds.Select(t => (T: t, Kappa: Agreement.CohensKappa(truth, [.. scores.Select(s => s >= t)]))).MaxBy(x => x.Kappa);
            var tunedText = scores.All(s => s is 0 or 1) ? "n/a" : $"{tuned.Kappa:0.00} at {tuned.T:0.00}";
            Console.WriteLine($"{name,-36} {Agreement.CohensKappa(truth, predicted),18:0.00} {$"{Caught(truth, predicted)}/{truth.Count(t => !t)}",7} " +
                $"{$"{FalseAlarms(truth, predicted)}/{truth.Count(t => t)}",13} {Auc(truth, scores),5:0.00} {tunedText,14} {ms,9:0}");
        }

        Row("quote", "Quote found in its source (Ch. 14)", [.. items.Select(i => i.Claim.QuoteFound ? 1.0 : 0.0)], 0);
        foreach (var (which, label) in new[] { ("three-way", "3-way xsmall"), ("binary", "Binary xsmall"), ("base", "3-way base"), ("base-quint8", "3-way base 8-bit") })
        {
            if (!Lab.NliAvailable(which))
            {
                Console.WriteLine($"{label,-36} not downloaded: run `python eng/get-models.py` for it");
                continue;
            }

            using var model = Lab.Nli(which);
            model.Entailment([("Warm-up.", "Warm-up.")]);
            foreach (var (mode, window) in new[] { ("whole block", int.MaxValue), ("block and windows", 3) })
            {
                if (which == "base-quint8" && window != 3) continue;
                var clock = Stopwatch.StartNew();
                var scores = new ClaimSupport(model, window).Score([.. items.Select(i => (Grounding.Premise(i.Block), i.Claim.Text))]);
                Row(window == 3 ? which : $"{which}, whole", $"{label}, {mode}", scores, clock.Elapsed.TotalMilliseconds / items.Count);
            }
        }

        // The detector the later experiments use, at every threshold, alone and with Chapter 14's quote check required too.
        var chosen = rows.Single(r => r.Key == Lab.SupportModel).Scores;
        Console.WriteLine();
        Console.WriteLine($"{"Threshold",9} {"Kappa",6} {"Caught",7} {"False alarms",13}   {"With a found quote: kappa",25} {"Caught",7} {"False alarms",13}");
        foreach (var t in thresholds)
        {
            var predicted = chosen.Select(s => s >= t).ToList();
            var both = predicted.Zip(items, (p, i) => p && i.Claim.QuoteFound).ToList();
            Console.WriteLine($"{t,9:0.00} {Agreement.CohensKappa(truth, predicted),6:0.00} {$"{Caught(truth, predicted)}/{truth.Count(x => !x)}",7} {$"{FalseAlarms(truth, predicted)}/{truth.Count(x => x)}",13}   " +
                $"{Agreement.CohensKappa(truth, both),25:0.00} {$"{Caught(truth, both)}/{truth.Count(x => !x)}",7} {$"{FalseAlarms(truth, both)}/{truth.Count(x => x)}",13}");
        }

        // Where the chosen detector and the labels disagree, for the error analysis.
        Console.WriteLine();
        foreach (var (item, score) in items.Zip(chosen).Where(p => p.First.Label.Supported != p.Second >= Lab.SupportThreshold))
        {
            Console.WriteLine($"  {(item.Label.Supported ? "missed support" : "false support "),-14} {score:0.00} {item.Label.Id} #{item.Label.Claim} {Shorten(item.Claim.Text, 90)}");
        }
    }

    /// <summary>One shipped claim against every premise its support is the best of: the whole block, then each window.</summary>
    public static void Explain(string id, int claim)
    {
        var answer = Lab.Answers("compressed", "dev")[id];
        var (c, block) = (answer.Claims[claim], answer.Evidence.First(b => b.Id == answer.Claims[claim].Source));
        using var model = Lab.Nli(Lab.SupportModel);
        var premises = new ClaimSupport(model).Premises(Grounding.Premise(block)).ToList();
        var entailment = model.Entailment([.. premises.Select(p => (p, c.Text))]);
        Console.WriteLine($"{id} claim {claim}, citing {c.Source}: {c.Text}");
        for (var i = 0; i < premises.Count; i++)
        {
            Console.WriteLine($"  {entailment[i],5:0.000}  {(i == 0 ? "whole block" : $"window {i}"),-12} {Shorten(premises[i], 100)}");
        }
    }

    /// <summary>
    /// Citation quality on a split, Chapter 3's markers against the shipped claims: ALCE's citation recall and precision
    /// with the binary NLI model as the judge of support, citations that point nowhere, unsupported statements that
    /// another block of the evidence would have supported, and whether the citations land on the labeled facts.
    /// </summary>
    public static void Citations(Lab lab, double threshold, string split = "dev")
    {
        using var model = Lab.Nli(Lab.SupportModel);
        var support = new ClaimSupport(model);
        var scorer = new CitationScorer(support, threshold);
        var perQuestion = new Dictionary<string, Dictionary<string, double>>();
        Console.WriteLine($"Golden set v2, {split}: answerable questions both setups answered; support = {model.Name}, P(entailment) >= {threshold:0.00}");
        Console.WriteLine();
        Console.WriteLine($"{"Setup",-30} {"Statements",10} {"Recall",7} {"Precision",9} {"Uncited",8} {"No source",9} {"Better block",12} {"Fact block",10} {"Fact span",9}");
        var both = Lab.Answers("chapter3", split).Where(a => Answered(a.Value)).Select(a => a.Key)
            .Intersect(Lab.Answers("compressed", split).Where(a => Answered(a.Value)).Select(a => a.Key))
            .Where(id => lab.Question(id).Answerable).Order(StringComparer.Ordinal).ToList();
        foreach (var (name, setup) in new[] { ("Chapter 3's [n] markers", "chapter3"), ("Shipped claims (Chapter 14)", "compressed") })
        {
            var answers = Lab.Answers(setup, split);
            var (total, uncited, fabricated, better, factBlock, factSpan) = (new CitationScore(0, 0, 0, 0), 0, 0, 0, 0, 0);
            perQuestion[setup] = [];
            foreach (var id in both)
            {
                var (answer, question) = (answers[id], lab.Question(id));
                var statements = Grounding.Statements(answer);
                var scores = statements.Select(s => scorer.Score([s])).ToList();
                perQuestion[setup][id] = scores.Count == 0 ? 0 : scores.Average(s => s.Supported);
                total = new CitationScore(total.Statements + statements.Count, total.Supported + scores.Sum(s => s.Supported), total.Citations + scores.Sum(s => s.Citations), total.Relevant + scores.Sum(s => s.Relevant));
                uncited += statements.Count(s => s.Cited.Count == 0);
                fabricated += Grounding.Fabricated(answer);

                // An unsupported statement that some other block of the evidence entails: the right fact, the wrong citation.
                var unsupported = statements.Where((s, i) => s.Cited.Count > 0 && scores[i].Supported == 0).ToList();
                var others = unsupported.SelectMany(s => answer.Evidence.Select(Grounding.Premise).Where(p => !s.Cited.Contains(p)).Select(p => (Statement: s, Premise: p))).ToList();
                var entailed = support.Score([.. others.Select(o => (o.Premise, o.Statement.Text))]);
                better += others.Zip(entailed).Where(p => p.Second >= threshold).Select(p => p.First.Statement).Distinct().Count();

                // Whether a cited block holds a labeled fact, and (claims with quotes only) whether a quote lies on one.
                var spans = GoldenSet.Locate(question, lab.Documents);
                var cited = answer.Evidence.Where(b => statements.Any(s => s.Cited.Contains(Grounding.Premise(b)))).ToList();
                factBlock += cited.Any(b => spans.Any(s => Covers(b, s))) ? 1 : 0;
                factSpan += answer.Claims.Any(c => c.QuoteFound && spans.Any(s => s.Doc == c.DocumentId && c.Start < s.End && s.Start < c.End)) ? 1 : 0;
            }

            var spanText = setup == "chapter3" ? "n/a" : $"{factSpan / (double)both.Count:0.00}";
            Console.WriteLine($"{name,-30} {total.Statements,10} {total.Recall,7:0.00} {total.Precision,9:0.00} {uncited,8} {fabricated,9} {better,12} {factBlock / (double)both.Count,10:0.00} {spanText,9}");
        }

        var change = Bootstrap.Paired([.. both.Select(id => perQuestion["chapter3"][id])], [.. both.Select(id => perQuestion["compressed"][id])]);
        Console.WriteLine();
        Console.WriteLine($"{both.Count} questions. Citation recall per question, shipped minus Chapter 3: {Lab.Interval(change)}");
    }

    /// <summary>
    /// The regression check: per-question groundedness (ALCE citation recall with the binary NLI model) of each
    /// candidate's recorded dev answers against the baseline snapshot, overall and by question type. With
    /// --write-baseline, the shipped answers become the baseline first.
    /// </summary>
    public static void Regress(Lab lab, string baselinePath, IReadOnlyList<string> candidates, bool write, double tolerance, double threshold)
    {
        using var model = Lab.Nli(Lab.SupportModel);
        var scorer = new CitationScorer(new ClaimSupport(model), threshold);
        string Slice(string id) => lab.Question(id).Type;
        if (write)
        {
            var questions = Grounding.Score(Lab.Answers("compressed", "dev"), Slice, scorer);
            Grounding.Write(new Snapshot("compressed", "dev", lab.GoldenSetSha256[..12], Lab.SupportModel, threshold, DateTime.Now.ToString("yyyy-MM-dd"), questions), baselinePath);
            Console.WriteLine($"Wrote the baseline: {questions.Count(q => !double.IsNaN(q.Score))} scored answers to {Path.GetFileName(baselinePath)}");
            Console.WriteLine();
        }

        var baseline = Grounding.Read(baselinePath);
        Console.WriteLine($"Baseline: {baseline.Setup}, {baseline.Split}, created {baseline.Created}; tolerance {tolerance:0.00}");
        foreach (var setup in candidates)
        {
            var clock = Stopwatch.StartNew();
            var result = Regression.Compare(baseline.Questions, Grounding.Score(Lab.Answers(setup, baseline.Split), Slice, scorer), tolerance);
            Console.WriteLine();
            Console.WriteLine($"Candidate {setup} ({clock.Elapsed.TotalSeconds:0} s): {(result.Any(r => r.Failed) ? "FAIL" : "pass")}");
            Console.WriteLine($"  {"Slice",-12} {"Questions",9} {"Baseline",8} {"Candidate",9} {"Change [95% interval]",22}");
            foreach (var r in result)
            {
                Console.WriteLine($"  {r.Slice,-12} {r.Questions,9} {r.Baseline,8:0.00} {r.Candidate,9:0.00} {Lab.Interval(r.Change),22}{(r.Failed ? "  FAIL" : "")}");
            }
        }
    }

    /// <summary>
    /// Error analysis of the labeled sample: where each wrong answer lost its fact (retrieval, compression or the model)
    /// and why each unsupported claim isn't supported. Writes every failure as a case for the golden set's review.
    /// </summary>
    public static void Errors(Lab lab, double threshold)
    {
        var (shipped, uncompressed) = (Lab.Answers("compressed", "dev"), Lab.Answers("grounded", "dev"));
        var cases = new List<object>();
        Console.WriteLine("Wrong answers in the labeled sample, by where the fact was lost:");
        foreach (var label in Lab.AnswerLabels().Where(l => !l.Correct))
        {
            var question = lab.Question(label.Id);
            var spans = GoldenSet.Locate(question, lab.Documents);
            bool Holds(Answer a) => spans.GroupBy(s => s.Fact).All(f => f.Any(s => a.Evidence.Any(b => Covers(b, s))));
            var stage = !Holds(uncompressed[label.Id]) ? "retrieval: not in the top four (FP2)"
                : !Holds(shipped[label.Id]) ? "compression dropped it (FP3)"
                : shipped[label.Id].Status is AnswerStatus.NotFound ? "declined with the fact in context (FP4)"
                : "in context, answered wrongly or in part (FP4/FP7)";
            Console.WriteLine($"  {label.Id,-8} {question.Type,-10} {stage}");
            Console.WriteLine($"  {"",-8} {label.Note}");
            cases.Add(new { label.Id, Kind = "wrong answer", Stage = stage, label.Note });
        }

        using var model = Lab.Nli(Lab.SupportModel);
        var support = new ClaimSupport(model);
        Console.WriteLine();
        Console.WriteLine("Unsupported claims in the labeled sample, by cause:");
        var causes = new List<(string Cause, string Type, string Id)>();
        foreach (var label in Lab.ClaimLabels().Where(l => !l.Supported))
        {
            var answer = shipped[label.Id];
            var claim = answer.Claims[label.Claim];
            var others = answer.Evidence.Where(b => b.Id != label.Source).ToList();
            var elsewhere = others.Count > 0 && support.Score([.. others.Select(b => (Grounding.Premise(b), claim.Text))]).Max() >= threshold;
            var cause = elsewhere ? "another block supports it: wrong citation"
                : !claim.QuoteFound ? "no block supports it; its quote isn't in its source"
                : "no block supports it; its quote is real";
            causes.Add((cause, lab.Question(label.Id).Type, label.Id));
            cases.Add(new { label.Id, Kind = "unsupported claim", label.Claim, Stage = cause, label.Note });
        }

        foreach (var group in causes.GroupBy(c => c.Cause).OrderByDescending(g => g.Count()))
        {
            var types = string.Join(", ", group.GroupBy(c => c.Type).OrderByDescending(t => t.Count()).Select(t => $"{t.Key} {t.Count()}"));
            Console.WriteLine($"  {group.Count(),3}  {group.Key,-52} {types}");
        }

        var path = Path.Combine(Lab.Runs, "ch15", "failure-cases.dev.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, cases.Select(c => JsonSerializer.Serialize(c, JsonSerializerOptions.Web)));
        Console.WriteLine();
        Console.WriteLine($"Wrote {cases.Count} failure cases to {Path.GetRelativePath(Path.Combine(Lab.Runs, ".."), path)}");
    }

    public static bool Answered(Answer answer) => answer.Status is AnswerStatus.Answered or AnswerStatus.Partial;

    public static bool Covers(EvidenceBlock block, LocatedSpan span) =>
        block.DocumentId == span.Doc && block.Segments.Any(s => s.Start < span.End && span.Start < s.End);

    public static double Agree(IReadOnlyList<bool> truth, IReadOnlyList<bool> predicted) => truth.Zip(predicted).Count(p => p.First == p.Second) / (double)truth.Count;

    /// <summary>Unsupported claims the detector flags.</summary>
    public static int Caught(IReadOnlyList<bool> truth, IReadOnlyList<bool> predicted) => truth.Zip(predicted).Count(p => !p.First && !p.Second);

    /// <summary>Supported claims the detector flags.</summary>
    public static int FalseAlarms(IReadOnlyList<bool> truth, IReadOnlyList<bool> predicted) => truth.Zip(predicted).Count(p => p.First && !p.Second);

    /// <summary>The probability that a supported claim scores above an unsupported one, ties counting half.</summary>
    public static double Auc(IReadOnlyList<bool> truth, IReadOnlyList<double> scores)
    {
        var positive = scores.Where((_, i) => truth[i]).ToList();
        var negative = scores.Where((_, i) => !truth[i]).ToList();
        return positive.Sum(p => negative.Sum(n => p > n ? 1.0 : p == n ? 0.5 : 0)) / (positive.Count * (double)negative.Count);
    }

    public static string Shorten(string text, int length)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= length ? flat : flat[..(length - 1)] + "…";
    }
}
