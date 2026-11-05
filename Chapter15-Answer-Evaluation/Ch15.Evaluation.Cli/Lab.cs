using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Generation;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Ch15.Evaluation.Cli;

/// <summary>
/// What every experiment shares: golden set v2, the corpus's documents (for the labeled spans), the answers Chapter 14
/// recorded, the labels written for this chapter, the NLI models and llama3.2 as a judge, its calls recorded.
/// </summary>
public sealed class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public required string GoldenSetSha256 { get; init; }

    public static string Runs { get; } = Path.GetFullPath(Path.Combine(EmbeddingModels.Root, "..", "runs"));

    public static async Task<Lab> LoadAsync()
    {
        var documents = new Dictionary<string, SourceDocument>(StringComparer.Ordinal);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var m = truth.Metadata;
            documents[entry.Id] = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, new Contoso.Knowledge.Chunking.ChunkSource(entry.Id, m.Title, truth).Text);
        }

        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        return new Lab { Documents = documents, Questions = GoldenSet.Load(path), GoldenSetSha256 = GoldenSet.Sha256(path) };
    }

    public GoldenQuestion Question(string id) => Questions.First(q => q.Id == id);

    /// <summary>The answers one of Chapter 14's setups recorded for a split, by question.</summary>
    public static IReadOnlyDictionary<string, Answer> Answers(string setup, string split)
    {
        var path = Path.Combine(Runs, "ch14", "answers", $"{setup}-{split}.jsonl");
        if (!File.Exists(path)) throw new FileNotFoundException($"Run `dotnet run --project Chapter14-Grounded-Generation/Ch14.Generation.Cli -- answer --setup {setup} --split {split}` first.", path);
        return ReadAnswers(path);
    }

    /// <summary>Answers in Chapter 14's format: one JSON object a line, the question's ID and the answer.</summary>
    public static IReadOnlyDictionary<string, Answer> ReadAnswers(string path)
    {
        var json = new JsonSerializerOptions(JsonSerializerOptions.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        return File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<RecordedAnswer>(l, json)!).ToDictionary(r => r.Id, r => r.Answer);
    }

    public static IReadOnlyList<ClaimLabel> ClaimLabels(string file = "claims.v1.jsonl") => ReadLines<ClaimLabel>(Labels(file));

    public static IReadOnlyList<AnswerLabel> AnswerLabels(string file = "answers.v1.jsonl") => ReadLines<AnswerLabel>(Labels(file));

    private static IReadOnlyList<T> ReadLines<T>(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<T>(l, JsonSerializerOptions.Web)!)];

    /// <summary>The labeled facts of a question as the judge's reference: the text of each labeled span.</summary>
    public IReadOnlyList<string> References(GoldenQuestion question) =>
        [.. GoldenSet.Locate(question, Documents).Select(s => Documents[s.Doc].Text[s.Start..s.End].Trim()).Distinct()];

    /// <summary>Chapter 14's 60-question sample of dev, drawn the same way: answerable questions in SHA-256 order of their ids.</summary>
    public IReadOnlyList<GoldenQuestion> Sample(int answerable = 60) =>
        [.. Questions.Where(q => q.Split == "dev" && q.Answerable).OrderBy(q => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(q.Id))), StringComparer.Ordinal).Take(answerable)];

    /// <summary>The support detector the NLI experiment settled on (Section 15.3): a model and its threshold.</summary>
    public const string SupportModel = "three-way";

    public const double SupportThreshold = 0.05;

    public static OnnxNliModel Nli(string which = SupportModel) => which switch
    {
        "binary" => new("DeBERTa-v3 xsmall, binary", NliFolder(which), ["entailment", "not_entailment"]),
        "three-way" => new("DeBERTa-v3 xsmall, 3-way", NliFolder(which), ThreeWay),
        "base" => new("DeBERTa-v3 base, 3-way", NliFolder(which), ThreeWay),
        "base-quint8" => new("DeBERTa-v3 base, 3-way, 8-bit", NliFolder(which), ThreeWay, "model_quint8_avx2.onnx"),
        _ => throw new ArgumentException($"Unknown NLI model '{which}'."),
    };

    /// <summary>Whether `python eng/get-models.py` has fetched the model's ONNX file.</summary>
    public static bool NliAvailable(string which) =>
        File.Exists(Path.Combine(NliFolder(which), which == "base-quint8" ? "model_quint8_avx2.onnx" : "model.onnx"));

    public static string NliFolder(string which) => Path.Combine(EmbeddingModels.Root, which switch
    {
        "binary" => "deberta-v3-xsmall-nli-binary",
        "base" or "base-quint8" => "nli-deberta-v3-base",
        _ => "nli-deberta-v3-xsmall",
    });

    private static readonly string[] ThreeWay = ["contradiction", "entailment", "neutral"];

    /// <summary>llama3.2 through Ollama on the CPU, every call recorded with its timings and replayed on reruns.</summary>
    public static RecordedChatClient Judge(string recordings = "judge-recordings.jsonl") => new(Ollama(), Path.Combine(Runs, "ch15", recordings));

    /// <summary>llama3.2 through Ollama on the CPU with Chapter 14's 8,192-token context.</summary>
    public static IChatClient Ollama() =>
        new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = "llama3.2", OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 8192))
            .Build();

    public static string Labels(string file) => Path.Combine(AppContext.BaseDirectory, "labels", file);

    /// <summary>The time and tokens of every call a recording holds, in the order they were recorded.</summary>
    public static IReadOnlyList<(double TotalMs, long InputTokens, long OutputTokens)> RecordedCalls(string recordings) =>
        !File.Exists(Path.Combine(Runs, "ch15", recordings)) ? [] :
        [.. File.ReadLines(Path.Combine(Runs, "ch15", recordings)).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => (e.GetProperty("TotalMs").GetDouble(), e.GetProperty("InputTokens").GetInt64(), e.GetProperty("OutputTokens").GetInt64()))];

    public static string Signed(double x) => (Math.Round(x, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("+0.00;-0.00");

    public static string Interval(Difference d) => $"{Signed(d.Mean)} [{Signed(d.Low)}, {Signed(d.High)}]";

    private sealed record RecordedAnswer(string Id, Answer Answer);
}

/// <summary>A person's verdict on one claim: does the source it cites support it?</summary>
public sealed record ClaimLabel(string Id, int Claim, string Text, string Source, bool Supported, string? Note = null);

/// <summary>A person's verdict on one answer: does it give the labeled facts correctly?</summary>
public sealed record AnswerLabel(string Id, bool Correct, string? Note = null);
