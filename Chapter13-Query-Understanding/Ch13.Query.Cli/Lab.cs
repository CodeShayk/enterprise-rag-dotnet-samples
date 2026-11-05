using System.Text.Json;
using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using OllamaSharp;

namespace Ch13.Query.Cli;

/// <summary>
/// What every experiment shares, as in Chapters 11 and 12 (copied, because chapters don't reference each other):
/// the corpus and its parent–child chunks, golden set v2, the harness, Chapter 11's hybrid retriever and, for the LLM
/// steps, llama3.2 through Ollama with every call cached.
/// </summary>
public sealed class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public required Harness Harness { get; init; }

    public required string Split { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static async Task<Lab> LoadAsync(string split)
    {
        var documents = new Dictionary<string, SourceDocument>(StringComparer.Ordinal);
        var chunks = new List<IndexedChunk>();
        var chunker = new ParentChildChunker(O200k, 100, 600);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;   // the archived revision 2
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            var document = documents[entry.Id] = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                chunks.Add(new IndexedChunk(chunks.Count, chunk, document, Collection(m.DocumentType), m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        return new Lab
        {
            Documents = documents,
            Chunks = chunks,
            Questions = [.. GoldenSet.Load(path).Where(q => split == "all" || q.Split == split)],
            Harness = new Harness([.. documents.Values], GoldenSet.Sha256(path)),
            Split = split,
        };
    }

    public IReadOnlyList<GoldenQuestion> Answerable => [.. Questions.Where(q => q.Answerable)];

    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" => "notices and reports",
        _ => "policies and bulletins",
    };

    /// <summary>bge-small INT8 with spinning off, as in Chapter 12.</summary>
    public static OnnxEmbeddingModel Bge() => new(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);

    public async Task<(HybridRetriever Hybrid, LuceneRetriever Keyword, DenseRetriever Dense)> HybridAsync(OnnxEmbeddingModel model)
    {
        var keyword = new LuceneRetriever("keyword", Chunks, ContosoAnalyzers.Identifiers());
        var dense = await DenseRetriever.CreateAsync("dense", Chunks, new OnnxEmbeddingGenerator(model), new OnnxEmbeddingGenerator(model, queries: true));
        return (new HybridRetriever("hybrid (Chapter 11)", [keyword, dense], new ReciprocalRankFusion(60)), keyword, dense);
    }

    /// <summary>llama3.2 through Ollama on the CPU, the model of earlier chapters.</summary>
    public static IChatClient Chat() =>
        new ChatClientBuilder(AIClientFactory.CreateChatClient(new AIOptions { Provider = AIProvider.Ollama, OllamaModel = "llama3.2", OllamaEndpoint = "http://127.0.0.1:11434" }))
            .ConfigureOptions(o => o.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, 4096))
            .Build();

    /// <summary>Whole words in the WordPiece vocabulary: a dictionary of everyday English the spelling corrector leaves alone.</summary>
    public static IReadOnlyList<string> KnownWords() =>
        [.. File.ReadLines(Path.Combine(EmbeddingModels.Root, "bge-small-en-v1.5", "vocab.txt")).Where(w => w.Length > 1 && w.All(char.IsLetter))];

    /// <summary>The intent examples: the first version, or with the paired in-scope examples added.</summary>
    public static IReadOnlyList<(string Text, Intent Intent)> IntentExamples(bool paired) =>
        [.. Examples("intents.json", "examples", Enum.Parse<Intent>), .. paired ? Examples("intents.json", "paired", Enum.Parse<Intent>) : []];

    public static LlmCache Cache() => new(Path.Combine(EmbeddingModels.Root, "..", "runs", "ch13", "llm-cache.jsonl"));

    public static IReadOnlyList<(string Text, T Label)> Examples<T>(string file, string section, Func<string, T> parse)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)));
        return [.. document.RootElement.GetProperty(section).EnumerateObject()
            .SelectMany(p => p.Value.EnumerateArray().Select(e => (e.GetString()!, parse(p.Name))))];
    }

    /// <summary>Runs a retriever over questions, each searched with its planned query and filter, if any.</summary>
    public Task<RunRecord> RunAsync(IRetriever retriever, IReadOnlyList<GoldenQuestion>? questions = null,
        IReadOnlyDictionary<string, (string Query, RetrievalFilter? Filter)>? plans = null, int k = 4) =>
        Harness.RunAsync(new Configuration(retriever, Chunks, plans), questions ?? Questions, k, Split);

    public static string Signed(double x) => (Math.Round(x, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("+0.00;-0.00");

    public static string Interval(Difference d) => $"{Signed(d.Mean)} [{Signed(d.Low)}, {Signed(d.High)}]";

    public static Difference Compare(RunRecord baseline, RunRecord candidate, string metric = "Sufficient") =>
        Bootstrap.Paired([.. baseline.Answerable.Select(q => RunRecord.Value(q, metric))], [.. candidate.Answerable.Select(q => RunRecord.Value(q, metric))]);
}

/// <summary>An <see cref="IRetriever"/> as a configuration for Chapter 5's harness; a plan can replace the query and add a filter.</summary>
public sealed class Configuration(IRetriever retriever, IReadOnlyList<IndexedChunk> chunks, IReadOnlyDictionary<string, (string Query, RetrievalFilter? Filter)>? plans = null)
    : IRetrievalUnderTest
{
    public string Name => retriever.Name;

    public IReadOnlyDictionary<string, string> Parameters { get; } = new Dictionary<string, string> { ["retriever"] = retriever.Name };

    public IReadOnlyList<ChunkSpan> Chunks { get; } = [.. chunks.Select(c => new ChunkSpan(c.Chunk.Doc, c.Chunk.ReturnStart, c.Chunk.ReturnEnd)).Distinct()];

    public async Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var (query, filter) = plans?.GetValueOrDefault(question) ?? (question, null);
        var result = await retriever.RetrieveAsync(new RetrievalRequest(query ?? question, asker, top, filter), cancellationToken);
        return [.. result.Hits.Select(h => new ChunkSpan(h.Chunk.Chunk.Doc, h.Chunk.Chunk.ReturnStart, h.Chunk.Chunk.ReturnEnd))];
    }
}
