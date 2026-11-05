using System.Diagnostics;
using System.Text.Json;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Query;

/// <summary>
/// Remembers every LLM call by step and prompt in a JSON Lines file, with the time the call took, so that an
/// experiment can be rerun without a model and still report the latency of the run that produced each answer.
/// </summary>
public sealed class LlmCache
{
    private readonly string _path;
    private readonly Dictionary<string, (string Text, double Ms)> _entries = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LlmCache(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            var entry = JsonSerializer.Deserialize<Entry>(line)!;
            _entries[Key(entry.Step, entry.Prompt)] = (entry.Text, entry.Ms);
        }
    }

    public int Calls { get; private set; }

    public double Milliseconds { get; private set; }

    public async Task<string> GetAsync(IChatClient chat, string step, string prompt, int maxTokens, CancellationToken cancellationToken = default, ChatResponseFormat? format = null)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_entries.TryGetValue(Key(step, prompt), out var entry))
            {
                var stopwatch = Stopwatch.StartNew();
                var response = await chat.GetResponseAsync(prompt, new ChatOptions { Temperature = 0, MaxOutputTokens = maxTokens, ResponseFormat = format }, cancellationToken);
                entry = (response.Text.Trim(), stopwatch.Elapsed.TotalMilliseconds);
                _entries[Key(step, prompt)] = entry;
                await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(new Entry(step, prompt, entry.Text, entry.Ms)) + "\n", cancellationToken);
            }

            Calls++;
            Milliseconds += entry.Ms;
            return entry.Text;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Raw string literals take the source file's line endings, so the same prompt can arrive with CRLF or LF.
    private static string Key(string step, string prompt) => step + "\u0001" + prompt.ReplaceLineEndings("\n");

    private sealed record Entry(string Step, string Prompt, string Text, double Ms);
}

/// <summary>Turns a follow-up into a standalone question, using the user's previous turns.</summary>
public sealed class Condenser(IChatClient chat, LlmCache cache, int window = 3)
{
    public Task<string> CondenseAsync(IReadOnlyList<string> history, string followUp, CancellationToken cancellationToken = default)
    {
        var turns = string.Join("\n", history.TakeLast(window).Select(h => $"User: {h}"));
        var prompt = $"""
            Rewrite the user's last question so that it can be understood without the conversation. Name everything it refers to,
            keep part numbers, error codes and equipment tags exactly as written, and don't answer it. Reply with the question only.

            {turns}
            User: {followUp}
            """;
        return cache.GetAsync(chat, "condense", prompt, 60, cancellationToken).ContinueWith(t => FirstLine(t.Result), cancellationToken);
    }

    internal static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.Trim('"') ?? text;
}

/// <summary>
/// LLM rewrites of a question for search, guarded against drift: a rewrite whose embedding strays below
/// <c>minimumSimilarity</c> from the original is thrown away and the original is used.
/// </summary>
public sealed class Rewriter(IChatClient chat, LlmCache cache, IEmbeddingGenerator<string, Embedding<float>> embedder, double minimumSimilarity = 0.8)
{
    public int Rejected { get; private set; }

    public async Task<string> RewriteAsync(string question, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            Rewrite this question from a maintenance technician as a clear search query for Contoso's manuals, procedures and records.
            Keep part numbers, error codes and equipment tags exactly as written. Reply with the query only.

            Question: {question}
            """;
        var rewrite = Condenser.FirstLine(await cache.GetAsync(chat, "rewrite", prompt, 60, cancellationToken));
        var vectors = await embedder.GenerateAsync([question, rewrite], cancellationToken: cancellationToken);
        if (System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(vectors[0].Vector.Span, vectors[1].Vector.Span) >= minimumSimilarity) return rewrite;
        Rejected++;
        return question;
    }

    /// <summary>Several search queries for one question, near-duplicates removed, for multi-query fusion.</summary>
    public async Task<IReadOnlyList<string>> VariantsAsync(string question, int count = 3, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            Write {count} different search queries that would find the answer to this question in Contoso's maintenance documents,
            one per line, without numbering. Keep part numbers, error codes and equipment tags exactly as written.

            Question: {question}
            """;
        var lines = (await cache.GetAsync(chat, "variants", prompt, 120, cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('-', '*', ' ', '1', '2', '3', '4', '5', '.', ')').Trim('"')).Where(l => l.Length > 3).Take(count).ToList();
        var variants = new List<string> { question };
        var vectors = new List<ReadOnlyMemory<float>> { (await embedder.GenerateAsync([question], cancellationToken: cancellationToken))[0].Vector };
        foreach (var line in lines)
        {
            var vector = (await embedder.GenerateAsync([line], cancellationToken: cancellationToken))[0].Vector;
            if (vectors.Any(v => System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(v.Span, vector.Span) > 0.95)) continue;   // a near-duplicate
            (variants, vectors) = ([.. variants, line], [.. vectors, vector]);
        }

        return variants;
    }

    /// <summary>The questions inside a question: one line each, or the question itself.</summary>
    public async Task<IReadOnlyList<string>> DecomposeAsync(string question, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            If this question asks for more than one thing, split it into separate standalone questions, one per line, each naming
            what it is about. If it asks for one thing, reply with the question unchanged. Reply with questions only.

            Question: {question}
            """;
        var parts = (await cache.GetAsync(chat, "decompose", prompt, 120, cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('-', '*', ' ', '1', '2', '3', '4', '.', ')').Trim('"')).Where(l => l.EndsWith('?')).Take(4).ToList();
        return parts.Count == 0 ? [question] : parts;
    }

    /// <summary>HyDE: a short passage that answers the question as a manual would, to be embedded as a passage.</summary>
    public Task<string> HypotheticalAsync(string question, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            Write a short passage, three sentences at most, from an industrial pump or gearbox maintenance manual that answers
            this question. Write it as the manual would; don't mention the question.

            Question: {question}
            """;
        return cache.GetAsync(chat, "hyde", prompt, 120, cancellationToken);
    }

    private static readonly ChatResponseFormat FiltersFormat = ChatResponseFormat.ForJsonSchema(JsonDocument.Parse("""
        {"type": "object", "properties": {
          "equipment": {"type": "array", "items": {"type": "string"}},
          "documentTypes": {"type": "array", "items": {"type": "string", "enum": ["sop", "manual", "troubleshooting", "safety-bulletin", "work-orders", "service-report", "contract", "policy"]}}},
         "required": ["equipment", "documentTypes"]}
        """).RootElement, "filters", "The filters a question states explicitly.");

    /// <summary>How many of the LLM's filter replies weren't JSON this reader could use.</summary>
    public int FilterParseFailures { get; private set; }

    /// <summary>
    /// Filters read by the LLM into a JSON object, for comparison with <see cref="RuleFilterExtractor"/>: the form shown
    /// in the prompt, and with <paramref name="schema"/> a JSON schema the server constrains the output to as well.
    /// </summary>
    public async Task<(IReadOnlyList<string> Equipment, IReadOnlyList<string> Types)> FiltersAsync(string question, bool schema = false, CancellationToken cancellationToken = default)
    {
        var prompt = $$"""
            Read the filters this question states explicitly. Reply with JSON only, in this form:
            {"equipment": ["P-201"], "documentTypes": ["sop"]}
            equipment: equipment tags or models named in the question (such as P-201, V-114, GBX-07, CP-200, HG-400, VD-75).
            documentTypes: only types the question names, from: sop, manual, troubleshooting, safety-bulletin, work-orders,
            service-report, contract, policy. Use empty lists when the question names none.

            Question: {{question}}
            """;
        var text = await cache.GetAsync(chat, schema ? "filters-schema" : "filters", prompt, 80, cancellationToken, schema ? FiltersFormat : null);
        try
        {
            var json = text[text.IndexOf('{')..(text.LastIndexOf('}') + 1)];
            using var document = JsonDocument.Parse(json);
            IReadOnlyList<string> List(string name) => document.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
                ? [.. p.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0)] : [];
            return (List("equipment"), List("documentTypes"));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException)
        {
            FilterParseFailures++;
            return ([], []);
        }
    }
}

/// <summary>Retrieves with each query from <c>queries</c> and fuses the rankings: multi-query, decomposition and rewriting in one shape.</summary>
public sealed class MultiQueryRetriever(string name, IRetriever inner, Func<string, CancellationToken, Task<IReadOnlyList<string>>> queries, IFusion fusion, int candidates = 20) : IRetriever
{
    public string Name { get; } = name;

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var rankings = new List<IReadOnlyList<Hit>>();
        foreach (var query in await queries(request.Question, cancellationToken))
        {
            rankings.Add((await inner.RetrieveAsync(request with { Question = query, Top = candidates }, cancellationToken)).Hits);
        }

        return RetrievalResult.Of(rankings.Count == 1 ? Ranking.DistinctReturns(rankings[0], request.Top) : fusion.Fuse(rankings, request.Top));
    }
}

/// <summary>HyDE in a hybrid: keyword search on the question, dense search on a hypothetical passage, fused.</summary>
public sealed class HydeRetriever(string name, IRetriever keyword, IRetriever passageSideDense, Rewriter rewriter, IFusion fusion, int candidates = 20) : IRetriever
{
    public string Name { get; } = name;

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var passage = await rewriter.HypotheticalAsync(request.Question, cancellationToken);
        var lexical = await keyword.RetrieveAsync(request with { Top = candidates }, cancellationToken);
        var dense = await passageSideDense.RetrieveAsync(request with { Question = passage, Top = candidates }, cancellationToken);
        return RetrievalResult.Of(fusion.Fuse([lexical.Hits, dense.Hits], request.Top));
    }
}
