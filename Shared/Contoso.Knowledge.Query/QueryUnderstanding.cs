using System.Diagnostics;
using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Query;

public enum Intent
{
    InScope,
    OutOfScope,
    ChitChat,
    Unsafe,
}

public sealed record IntentResult(Intent Intent, double Margin, bool UsedLlm);

/// <summary>
/// Classifies a question by its nearest written examples, embedded with bge-small: in scope, out of scope, chit-chat
/// or unsafe. When the two best intents are within <c>margin</c> of each other, an LLM decides instead.
/// </summary>
public sealed class IntentClassifier
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly List<(float[] Vector, Intent Intent)> _examples;
    private readonly Func<string, CancellationToken, Task<Intent>>? _fallback;
    private readonly double _margin;
    private readonly bool _confirmDeclines;

    private IntentClassifier(IEmbeddingGenerator<string, Embedding<float>> embedder, List<(float[], Intent)> examples,
        Func<string, CancellationToken, Task<Intent>>? fallback, double margin, bool confirmDeclines) =>
        (_embedder, _examples, _fallback, _margin, _confirmDeclines) = (embedder, examples, fallback, margin, confirmDeclines);

    /// <param name="confirmDeclines">Turn a question away only if the LLM also says it isn't in scope.</param>
    public static async Task<IntentClassifier> CreateAsync(IEmbeddingGenerator<string, Embedding<float>> embedder,
        IReadOnlyList<(string Text, Intent Intent)> examples, Func<string, CancellationToken, Task<Intent>>? fallback = null, double margin = 0.03,
        bool confirmDeclines = false)
    {
        var vectors = await embedder.GenerateAsync(examples.Select(e => e.Text));
        return new IntentClassifier(embedder, [.. vectors.Select((v, i) => (v.Vector.ToArray(), examples[i].Intent))], fallback, margin, confirmDeclines);
    }

    public async Task<IntentResult> ClassifyAsync(string question, CancellationToken cancellationToken = default)
    {
        var query = (await _embedder.GenerateAsync([question], cancellationToken: cancellationToken))[0].Vector.ToArray();
        // Each intent scores its most similar example; the margin is the gap between the best two intents.
        var scores = _examples.GroupBy(e => e.Intent)
            .Select(g => (Intent: g.Key, Score: g.Max(e => TensorPrimitives.CosineSimilarity(query, e.Vector))))
            .OrderByDescending(s => s.Score).ToList();
        var margin = scores[0].Score - scores[1].Score;
        if (_fallback is not null && _confirmDeclines && scores[0].Intent != Intent.InScope)
        {
            // A decline needs a second opinion: the examples match on topic, the LLM reads what is asked.
            var second = await _fallback(question, cancellationToken);
            return new IntentResult(second == Intent.InScope ? Intent.InScope : scores[0].Intent, margin, true);
        }

        if (margin >= _margin || _fallback is null) return new IntentResult(scores[0].Intent, margin, false);
        return new IntentResult(await _fallback(question, cancellationToken), margin, true);
    }
}

/// <summary>
/// Corrects misspelled words against the vocabulary of the index itself, so a correction can only produce a word the
/// documents use. Identifiers, short words and known words are never touched.
/// </summary>
public sealed partial class SpellingCorrector
{
    private readonly Dictionary<string, int> _counts;
    private readonly HashSet<string> _known;

    /// <param name="texts">The index's text: corrections can only produce its words.</param>
    /// <param name="knownWords">Words never corrected even if the index lacks them, such as the WordPiece vocabulary's whole words.</param>
    public SpellingCorrector(IEnumerable<string> texts, IEnumerable<string>? knownWords = null)
    {
        _known = new HashSet<string>(knownWords ?? [], StringComparer.Ordinal);
        _counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in texts.SelectMany(t => Word().Matches(t.ToLowerInvariant()).Select(m => m.Value)))
        {
            _counts[word] = _counts.GetValueOrDefault(word) + 1;
        }
    }

    public int Vocabulary => _counts.Count;

    public (string Text, IReadOnlyList<(string From, string To)> Changes) Correct(string question)
    {
        var changes = new List<(string, string)>();
        var text = Word().Replace(question, m =>
        {
            var word = m.Value.ToLowerInvariant();
            if (word.Length < 4 || _counts.ContainsKey(word) || _known.Contains(word) || Identifiers.IsIdentifier(m.Value)) return m.Value;
            var limit = word.Length >= 8 ? 2 : 1;
            var best = _counts.Where(c => Math.Abs(c.Key.Length - word.Length) <= limit)
                .Select(c => (c.Key, c.Value, Distance: Distance(word, c.Key, limit)))
                .Where(c => c.Distance <= limit)
                .OrderBy(c => c.Distance).ThenByDescending(c => c.Value).FirstOrDefault();
            if (best.Key is null) return m.Value;
            changes.Add((m.Value, best.Key));
            return best.Key;
        });
        return (text, changes);
    }

    /// <summary>Damerau–Levenshtein distance (adjacent transpositions count as one edit), cut off above <paramref name="limit"/>.</summary>
    public static int Distance(string a, string b, int limit = int.MaxValue)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var rowMin = int.MaxValue;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                rowMin = Math.Min(rowMin, d[i, j]);
            }

            if (rowMin > limit) return limit + 1;
        }

        return d[a.Length, b.Length];
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();
}

/// <summary>Questions that ask for two things in one sentence: "What does E-217 mean, and how do I clear it?"</summary>
public static partial class Compound
{
    public static bool IsCompound(string question) => TwoPart().IsMatch(question);

    [GeneratedRegex(@"^((?:What|Which|How|Who|When|Why|Is|Does|Can)\b.+?), and (.+)\?$")]
    private static partial Regex TwoPart();
}

public static partial class Identifiers
{
    /// <summary>A token with letters and digits in it, such as PN-4471-C, E-217, P-201 or e217.</summary>
    public static bool IsIdentifier(string token) => Token().IsMatch(token) && Token().Match(token).Length == token.Length;

    public static IReadOnlyList<string> Find(string text) => [.. Token().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"\b(?=[A-Za-z-]*\d)(?=[\d-]*[A-Za-z])[A-Za-z\d]+(?:-[A-Za-z\d]+)*\b")]
    private static partial Regex Token();
}

/// <summary>
/// Filters that a question states, read with rules: equipment tags it names, expanded through the asset records to
/// the tags that documents actually carry, and document types it names in words. Nothing is guessed.
/// </summary>
public sealed partial class RuleFilterExtractor(IReadOnlyDictionary<string, IReadOnlyList<string>> assetTags)
{
    private static readonly (string Pattern, string[] Types)[] TypeWords =
    [
        (@"\b(SOP|standard operating procedure|procedure)s?\b", ["sop", "work-instruction"]),
        (@"\bmanuals?\b", ["manual", "troubleshooting"]),
        (@"\b(safety )?bulletins?\b", ["safety-bulletin"]),
        (@"\bwork orders?\b", ["work-orders"]),
        (@"\bservice reports?\b", ["service-report"]),
        (@"\bcontracts?\b", ["contract"]),
        (@"\bpolic(y|ies)\b", ["policy"]),
    ];

    /// <summary>Builds the expansion from asset records: each tagged asset brings the tags listed on its record, such as P-201's model.</summary>
    public static RuleFilterExtractor FromAssetRecords(IEnumerable<IndexedChunk> chunks) =>
        new(chunks.Where(c => c.DocumentType == "asset-record").GroupBy(c => c.Document.Id)
            .SelectMany(g => g.First().EquipmentTags.Select(tag => (tag, tags: g.First().EquipmentTags)))
            .GroupBy(x => x.tag).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.SelectMany(x => x.tags).Distinct()]));

    public RetrievalFilter? Extract(string question, bool expand = true, bool includeUntagged = true)
    {
        var tags = Equipment().Matches(question).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
        if (expand) tags = [.. tags.SelectMany(t => assetTags.GetValueOrDefault(t) ?? [t]).Distinct()];
        var types = TypeWords.Where(w => Regex.IsMatch(question, w.Pattern, RegexOptions.IgnoreCase)).SelectMany(w => w.Types).Distinct().ToList();
        if (tags.Count == 0 && types.Count == 0) return null;
        return new RetrievalFilter
        {
            EquipmentTags = tags.Count == 0 ? null : includeUntagged ? [.. tags, RetrievalFilter.Untagged] : tags,
            DocumentTypes = types.Count == 0 ? null : types,
        };
    }

    // Contoso's equipment tags: pumps, valves, gearboxes, drives and models, as Chapter 6's metadata writes them.
    [GeneratedRegex(@"\b(?:P-\d{3}|V-\d{3}|GBX-\d{2}|CP-200|HG-400|VD-75)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Equipment();
}

/// <summary>A semantic router: the route of the most similar written example, embedded with bge-small.</summary>
public sealed class SemanticRouter
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly List<(float[] Vector, string Route)> _examples;

    private readonly string _default;
    private readonly double _margin;

    private SemanticRouter(IEmbeddingGenerator<string, Embedding<float>> embedder, List<(float[], string)> examples, string @default, double margin) =>
        (_embedder, _examples, _default, _margin) = (embedder, examples, @default, margin);

    /// <param name="default">The route taken unless another one wins by at least <paramref name="margin"/>.</param>
    public static async Task<SemanticRouter> CreateAsync(IEmbeddingGenerator<string, Embedding<float>> embedder, IReadOnlyList<(string Text, string Route)> examples,
        string @default = "retrieve", double margin = 0)
    {
        var vectors = await embedder.GenerateAsync(examples.Select(e => e.Text));
        return new SemanticRouter(embedder, [.. vectors.Select((v, i) => (v.Vector.ToArray(), examples[i].Route))], @default, margin);
    }

    public async Task<string> RouteAsync(string question, CancellationToken cancellationToken = default)
    {
        var query = (await _embedder.GenerateAsync([question], cancellationToken: cancellationToken))[0].Vector.ToArray();
        var best = _examples.GroupBy(e => e.Route).Select(g => (Route: g.Key, Score: g.Max(e => TensorPrimitives.CosineSimilarity(query, e.Vector))))
            .OrderByDescending(r => r.Score).ToList();
        var stay = best.FirstOrDefault(r => r.Route == _default).Score;
        return best[0].Route == _default || best[0].Score - stay >= _margin ? best[0].Route : _default;
    }
}

/// <summary>
/// What the planner decided for one question: the queries to retrieve with (one, or one per part of a compound
/// question), the intent, the route, any filter, and what each step cost. Route "none" means no retrieval; "sql" is a
/// flag for Chapter 17's database tools, and retrieval still runs.
/// </summary>
public sealed record QueryPlan(string Original, IReadOnlyList<string> Queries, Intent Intent, string Route, RetrievalFilter? Filter,
    IReadOnlyList<string> Notes, IReadOnlyDictionary<string, double> Milliseconds)
{
    public bool Retrieve => Intent is Intent.InScope or Intent.OutOfScope && Route != "none";
}

/// <summary>
/// Plans a question before retrieval: checks its intent, makes a follow-up standalone, corrects spelling against the
/// index, reads the filters it states, picks a route and splits a compound question. Each step is optional, so each
/// can be measured. Out-of-scope questions are declined only when <c>declineOutOfScope</c> is set; otherwise retrieval
/// runs and Chapter 12's threshold decides. Without a condenser, a follow-up is joined to the previous turn.
/// </summary>
public sealed class QueryPlanner(IntentClassifier? intent, SpellingCorrector? spelling, RuleFilterExtractor? filters, Condenser? condenser, SemanticRouter? router,
    Rewriter? decomposer = null, bool declineOutOfScope = true)
{
    public async Task<QueryPlan> PlanAsync(string question, IReadOnlyList<string> history, CancellationToken cancellationToken = default)
    {
        var (notes, times) = (new List<string>(), new Dictionary<string, double>());
        var stopwatch = Stopwatch.StartNew();
        double Lap() { var ms = stopwatch.Elapsed.TotalMilliseconds; stopwatch.Restart(); return ms; }

        var kind = Intent.InScope;
        if (intent is not null)
        {
            var result = await intent.ClassifyAsync(question, cancellationToken);
            (kind, times["intent"]) = (result.Intent, Lap());
            if (result.UsedLlm) notes.Add("intent by LLM");
            if (kind is Intent.ChitChat or Intent.Unsafe || (kind == Intent.OutOfScope && declineOutOfScope))
            {
                return new QueryPlan(question, [question], kind, "none", null, notes, times);
            }
        }

        var query = question;
        if (condenser is not null && history.Count > 0)
        {
            (query, times["condense"]) = (await condenser.CondenseAsync(history, question, cancellationToken), Lap());
            notes.Add($"condensed: {query}");
        }
        else if (history.Count > 0)
        {
            // Measured as good as an LLM's condensation, and it can't invent anything.
            query = $"{history[^1]} {question}";
            notes.Add("joined to the previous turn");
        }

        if (spelling is not null)
        {
            var (corrected, changes) = spelling.Correct(query);
            (query, times["spelling"]) = (corrected, Lap());
            notes.AddRange(changes.Select(c => $"spelling: {c.From} → {c.To}"));
        }

        RetrievalFilter? filter = null;
        if (filters is not null) (filter, times["filters"]) = (filters.Extract(query), Lap());
        var route = "retrieve";
        if (router is not null) (route, times["route"]) = (await router.RouteAsync(query, cancellationToken), Lap());
        IReadOnlyList<string> queries = [query];
        if (decomposer is not null && Compound.IsCompound(query))
        {
            (queries, times["decompose"]) = (await decomposer.DecomposeAsync(query, cancellationToken), Lap());
            notes.Add($"decomposed into {queries.Count}");
        }

        return new QueryPlan(question, queries, kind, route, filter, notes, times);
    }
}
