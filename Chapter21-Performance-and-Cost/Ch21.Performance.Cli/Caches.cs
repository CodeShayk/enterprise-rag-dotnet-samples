using System.Diagnostics;
using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Performance;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Redis;
using static System.FormattableString;

namespace Ch21.Performance.Cli;

/// <summary>A pair of questions for the semantic cache: one that means the same, one that looks the same and doesn't.</summary>
public sealed record CachePair(string Id, string Question, string Paraphrase, string NearMiss, string Change);

/// <summary>The caching experiments: what each layer saves, a simulated workload, a key that leaks, semantic lookups and HybridCache.</summary>
public static class Caches
{
    /// <summary>The time each cacheable stage takes, the share of a day's requests a cache could serve, and a leak.</summary>
    public static async Task LayersAsync(Lab lab, int requests = 1000, double zipf = 1.0)
    {
        var generator = await lab.GeneratorAsync(Lab.AnswerChat(Lab.Large));
        var queries = new OnnxEmbeddingGenerator(lab.Bge, queries: true);
        var retriever = await lab.RetrieverAsync();
        var dev = lab.Split("dev").ToList();
        var (embed, retrieve) = (new List<double>(), new List<double>());
        var answers = new Dictionary<string, Answer>();
        foreach (var q in dev)
        {
            var clock = Stopwatch.StartNew();
            await queries.GenerateAsync([q.Question]);
            embed.Add(clock.Elapsed.TotalMilliseconds);
            clock.Restart();
            await retriever.RetrieveAsync(new RetrievalRequest(q.Question, People.Find(q.Asker)!, 4));
            retrieve.Add(clock.Elapsed.TotalMilliseconds);
            answers[q.Id] = await Lab.AnswerAsync(generator, q);
        }

        var model = answers.Values.Where(a => a.Timings.PromptTokens > 0).Select(a => a.Timings.TotalMs - a.Timings.RetrievalMs).ToList();
        Console.WriteLine($"What a hit saves, p50 over {dev.Count} dev questions");
        Console.WriteLine(Invariant($"  query embedding (bge-small)               {Stats.Percentile(embed, 0.5),8:F1} ms"));
        Console.WriteLine(Invariant($"  retrieval with re-ranking                 {Stats.Percentile(retrieve, 0.5),8:F1} ms"));
        Console.WriteLine(Invariant($"  the model call (recorded)                 {Stats.Percentile(model, 0.5) / 1000,8:F1} s"));

        // A simulated day: only the model call is cached, keyed by the evidence; retrieval runs for every request.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var (hits, calls, latencies, uncached) = (0, 0, new List<double>(), new List<double>());
        foreach (var q in Day(dev, requests, zipf))
        {
            var answer = answers[q.Id];
            uncached.Add(answer.Timings.TotalMs / 1000);
            var key = CacheKeys.Answer(GroundingPrompt.Version, Lab.Large, q.Question, answer.Evidence);
            var hit = answer.Timings.PromptTokens > 0 && !seen.Add(key);
            if (hit) hits++;
            else if (answer.Timings.PromptTokens > 0) calls++;
            latencies.Add(hit ? answer.Timings.RetrievalMs / 1000 : answer.Timings.TotalMs / 1000);
        }

        Console.WriteLine(Invariant($"\nA simulated day: {requests} requests, Zipf popularity (s = {zipf}) over {dev.Count} dev questions"));
        Console.WriteLine(Invariant($"  answers from the cache {hits} ({hits / (double)requests:P0}); model calls {calls}; abstained without the model {requests - hits - calls}"));
        Console.WriteLine(Invariant($"  latency without the cache: mean {uncached.Average(),5:F1} s, p50 {Stats.Percentile(uncached, 0.5),5:F1} s, p95 {Stats.Percentile(uncached, 0.95),5:F1} s"));
        Console.WriteLine(Invariant($"  latency with the cache:    mean {latencies.Average(),5:F1} s, p50 {Stats.Percentile(latencies, 0.5),5:F1} s, p95 {Stats.Percentile(latencies, 0.95),5:F1} s"));

        // The leak: a key with the question alone serves Grace's answer, from a procurement contract, to Tomás.
        var g017 = lab.Questions.Single(q => q.Id == "G017");
        var grace = answers["G017"];
        var tomas = await AnswerAsAsync(generator, g017.Question, People.Tomas);
        Console.WriteLine($"\nG017 asked by Grace, then by Tomás: \"{g017.Question}\"");
        Console.WriteLine($"  Grace's evidence: {string.Join(", ", grace.Evidence.Select(b => b.DocumentId).Distinct())}");
        Console.WriteLine($"  Tomás's evidence: {string.Join(", ", tomas.Evidence.Select(b => b.DocumentId).Distinct())}");
        var contract = lab.Documents["SC-2024-118"];
        Console.WriteLine($"  key = the question: Tomás gets Grace's answer, citing SC-2024-118 (Tomás may read it: {People.Tomas.CanRead(contract)})");
        Console.WriteLine($"    \"{grace.Text}\"");
        var same = CacheKeys.Answer(GroundingPrompt.Version, Lab.Large, g017.Question, grace.Evidence) == CacheKeys.Answer(GroundingPrompt.Version, Lab.Large, g017.Question, tomas.Evidence);
        Console.WriteLine($"  key with the evidence: same key for both: {same}; Tomás gets his own answer:");
        Console.WriteLine($"    \"{tomas.Text}\"");
    }

    /// <summary>
    /// A simulated day's requests: question popularity follows Zipf's law over a random ranking of the questions, each
    /// asked by its labeled asker. The seed makes every command draw the same day.
    /// </summary>
    public static IEnumerable<GoldenQuestion> Day(IReadOnlyList<GoldenQuestion> questions, int requests = 1000, double zipf = 1.0)
    {
        var random = new Random(21);
        var ranked = questions.OrderBy(_ => random.Next()).ToList();
        var weights = ranked.Select((_, k) => 1 / Math.Pow(k + 1, zipf)).ToList();
        var total = weights.Sum();
        for (var i = 0; i < requests; i++)
        {
            var pick = random.NextDouble() * total;
            var k = 0;
            while ((pick -= weights[k]) > 0) k++;
            yield return ranked[k];
        }
    }

    private static async Task<Answer> AnswerAsAsync(GroundedAnswerGenerator generator, string question, Person asker)
    {
        Answer? answer = null;
        await foreach (var e in generator.StreamAsync(question, asker))
        {
            if (e is AnswerReady ready) answer = ready.Answer;
        }

        return answer!;
    }

    /// <summary>
    /// Semantic lookups on hand-written pairs: a paraphrase should hit, a near miss (another part, pump or number) must
    /// not. bge-small's cosines at each threshold, with and without the identifier guard.
    /// </summary>
    public static async Task SemanticAsync(Lab lab)
    {
        var pairs = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "cache-pairs.v1.jsonl")).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<CachePair>(l, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToList();
        var embedder = new OnnxEmbeddingGenerator(lab.Bge, queries: true);
        async Task<float[]> Vector(string text) => (await embedder.GenerateAsync([text]))[0].Vector.ToArray();
        var rows = new List<(CachePair Pair, double Paraphrase, double NearMiss, bool ParaphraseIds, bool NearMissIds)>();
        foreach (var p in pairs)
        {
            var (q, a, b) = (await Vector(p.Question), await Vector(p.Paraphrase), await Vector(p.NearMiss));
            var ids = SemanticCache<string>.IdentifiersOf(p.Question);
            rows.Add((p, System.Numerics.Tensors.TensorPrimitives.Dot(q, a), System.Numerics.Tensors.TensorPrimitives.Dot(q, b),
                ids.SetEquals(SemanticCache<string>.IdentifiersOf(p.Paraphrase)), ids.SetEquals(SemanticCache<string>.IdentifiersOf(p.NearMiss))));
        }

        Console.WriteLine($"{pairs.Count} cached questions, each with a paraphrase (same answer) and a near miss (different answer)");
        Console.WriteLine(Invariant($"cosine p50: paraphrases {Stats.Percentile(rows.Select(r => r.Paraphrase), 0.5):F3}, near misses {Stats.Percentile(rows.Select(r => r.NearMiss), 0.5):F3}"));
        Console.WriteLine($"{"threshold",10} {"paraphrases hit",16} {"near misses hit",16} {"with the guard",15}");
        foreach (var t in new[] { 0.80, 0.85, 0.90, 0.93, 0.95, 0.97 })
        {
            Console.WriteLine(Invariant($"{t,10:F2} {rows.Count(r => r.Paraphrase >= t),16} {rows.Count(r => r.NearMiss >= t),16} {$"{rows.Count(r => r.Paraphrase >= t && r.ParaphraseIds)} / {rows.Count(r => r.NearMiss >= t && r.NearMissIds)}",15}"));
        }

        foreach (var r in rows.Where(r => r.NearMiss >= 0.95 && r.NearMissIds).Take(3))
        {
            Console.WriteLine(Invariant($"  passes the guard at {r.NearMiss:F3}: \"{r.Pair.Question}\" / \"{r.Pair.NearMiss}\""));
        }

        await File.WriteAllLinesAsync(Path.Combine(Lab.Runs, "semantic.jsonl"), rows.Select(r => JsonSerializer.Serialize(new { r.Pair.Id, r.Paraphrase, r.NearMiss, r.ParaphraseIds, r.NearMissIds })));
    }

    /// <summary>
    /// HybridCache with Redis in a container as the second tier: hit times on each tier, one computation for twenty
    /// concurrent requests, and tag invalidation seen from the node that removed the tag and from another one.
    /// </summary>
    public static async Task HybridAsync(Lab lab)
    {
        await using var redis = new RedisBuilder("redis:8.2.2").Build();
        await redis.StartAsync();
        var (nodeA, nodeB, nodeC) = (Node(redis.GetConnectionString()), Node(redis.GetConnectionString()), Node(redis.GetConnectionString()));
        var generator = await lab.GeneratorAsync(Lab.AnswerChat(Lab.Large));
        var answers = new List<(string Key, Answer Answer)>();
        foreach (var q in lab.Split("dev"))
        {
            var answer = await Lab.AnswerAsync(generator, q);
            if (answer.Timings.PromptTokens > 0) answers.Add((CacheKeys.Answer(GroundingPrompt.Version, Lab.Large, q.Question, answer.Evidence), answer));
        }

        var options = new HybridCacheEntryOptions { Expiration = TimeSpan.FromDays(1), LocalCacheExpiration = TimeSpan.FromMinutes(10) };
        async Task<(double Ms, bool Computed)> Get(HybridCache node, string key, Answer answer)
        {
            var computed = false;
            var clock = Stopwatch.StartNew();
            await node.GetOrCreateAsync(key, _ =>
            {
                computed = true;
                return ValueTask.FromResult(answer);
            }, options, CacheKeys.DocumentTags(answer.Evidence));
            return (clock.Elapsed.TotalMilliseconds, computed);
        }

        foreach (var (key, answer) in answers) await Get(nodeA, key, answer);
        var l1 = new List<double>();
        foreach (var (key, answer) in answers) l1.Add((await Get(nodeA, key, answer)).Ms);
        var l2 = new List<double>();
        foreach (var (key, answer) in answers) l2.Add((await Get(nodeB, key, answer)).Ms);
        Console.WriteLine($"{answers.Count} dev answers cached on node A (memory, then Redis {redis.Image.Tag} in a container)");
        Console.WriteLine(Invariant($"  hit on node A, from its memory:      p50 {Stats.Percentile(l1, 0.5) * 1000,7:F0} µs"));
        Console.WriteLine(Invariant($"  first hit on node B, from Redis:     p50 {Stats.Percentile(l2, 0.5),7:F2} ms"));

        // Twenty requests for an answer nobody has yet: one computation, nineteen waiters.
        var computations = 0;
        var stampede = Enumerable.Range(0, 20).Select(_ => nodeC.GetOrCreateAsync("stampede", async ct =>
        {
            Interlocked.Increment(ref computations);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return answers[0].Answer;
        }, options).AsTask());
        await Task.WhenAll(stampede);
        Console.WriteLine($"  20 concurrent requests for one missing key on node C: {computations} computation");

        // SOP-FS-014 re-indexed: node A removes the tag. Who still serves the old answers?
        var tagged = answers.Where(a => a.Answer.Evidence.Any(b => b.DocumentId == "SOP-FS-014")).ToList();
        await nodeA.RemoveByTagAsync("doc:SOP-FS-014");
        // Read on a new node first (Redis only), then on node B (its memory), then on node A, so no fresh write races a read.
        var nodeD = Node(redis.GetConnectionString());
        var (recomputedA, recomputedB, recomputedD) = (0, 0, 0);
        foreach (var (key, answer) in tagged) recomputedD += (await Get(nodeD, key, answer)).Computed ? 1 : 0;
        foreach (var (key, answer) in tagged) recomputedB += (await Get(nodeB, key, answer)).Computed ? 1 : 0;
        foreach (var (key, answer) in tagged) recomputedA += (await Get(nodeA, key, answer)).Computed ? 1 : 0;

        var untouched = 0;
        foreach (var (key, answer) in answers.Except(tagged))
        {
            if (!(await Get(nodeA, key, answer)).Computed) untouched++;
        }

        Console.WriteLine($"  RemoveByTagAsync(\"doc:SOP-FS-014\") on node A: {tagged.Count} answers carry the tag");
        Console.WriteLine($"    recomputed on node A {recomputedA} of {tagged.Count}; node B, which held them in memory, {recomputedB}; a new node D reading Redis {recomputedD}");
        Console.WriteLine($"    answers without the tag still cached on node A: {untouched} of {answers.Count - tagged.Count}");
    }

    private static HybridCache Node(string redis)
    {
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(o => o.Configuration = redis);
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}
