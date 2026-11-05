using System.Diagnostics;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Retrieval;
using Microsoft.ML.Tokenizers;

namespace Ch12.Reranking.Cli;

/// <summary>
/// What every experiment shares, as in Chapter 11 (copied, because chapters don't reference each other): the corpus,
/// its parent–child chunks with metadata, golden set v2 for one split, the harness and Chapter 11's hybrid retriever.
/// </summary>
public sealed class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyDictionary<string, DocumentMetadata> Metadata { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public required Harness Harness { get; init; }

    public required string Split { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static async Task<Lab> LoadAsync(string split, bool audited = false)
    {
        var documents = new Dictionary<string, SourceDocument>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, DocumentMetadata>(StringComparer.Ordinal);
        var chunks = new List<IndexedChunk>();
        var chunker = new ParentChildChunker(O200k, 100, 600);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && !e.Id.StartsWith("INTRANET-", StringComparison.Ordinal)))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;   // the archived revision 2
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var source = new ChunkSource(entry.Id, truth.Metadata.Title, truth);
            var m = truth.Metadata;
            var document = new SourceDocument(entry.Id, m.Title, m.Tenant, m.AllowedGroups, source.Text);
            (documents[entry.Id], metadata[entry.Id]) = (document, m);
            foreach (var chunk in await chunker.SplitAsync(source))
            {
                chunks.Add(new IndexedChunk(chunks.Count, chunk, document, Collection(m.DocumentType), m.DocumentType, m.EquipmentTags, m.EffectiveFrom, m.EffectiveTo));
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        var questions = GoldenSet.Load(path).Where(q => q.Split == split).ToList();
        if (audited) questions = [.. questions.Select(q => Audit(q, documents))];
        return new Lab
        {
            Documents = documents,
            Metadata = metadata,
            Chunks = chunks,
            Questions = questions,
            Harness = new Harness([.. documents.Values], GoldenSet.Sha256(path)),
            Split = split,
        };
    }

    public IEnumerable<GoldenQuestion> Answerable => Questions.Where(q => q.Answerable);

    /// <summary>
    /// The label audit of Section 12.7, as an overlay that leaves golden set v2 unchanged: a question labeled with a
    /// row of an error-code table also accepts the paragraph that explains that code, as an alternative for the same fact.
    /// </summary>
    public static GoldenQuestion Audit(GoldenQuestion question, IReadOnlyDictionary<string, SourceDocument> documents)
    {
        var added = new List<RelevantSpan>();
        foreach (var span in question.Relevant)
        {
            var code = System.Text.RegularExpressions.Regex.Match(span.Text, @"^\| ((?:E-|F)\d+) \|");
            if (!code.Success) continue;
            var label = code.Groups[1].Value.StartsWith('E') ? "Error" : "Fault";
            var paragraph = System.Text.RegularExpressions.Regex.Match(documents[span.Doc].Text, $@"{label} {code.Groups[1].Value} means[^\n]*");
            if (paragraph.Success) added.Add(span with { Text = paragraph.Value.TrimEnd() });
        }

        return added.Count == 0 ? question : question with { Relevant = [.. question.Relevant, .. added] };
    }

    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" => "notices and reports",
        _ => "policies and bulletins",
    };

    /// <summary>bge-small INT8 with spinning off: it shares the CPU with the cross-encoder's session (Section 12.3).</summary>
    public static OnnxEmbeddingModel Bge() => new(EmbeddingModels.BgeSmall(int8: true), 0, spinning: false);

    /// <summary>Chapter 11's hybrid retriever: Lucene with Contoso's analyzer and bge-small INT8, fused by RRF with k = 60.</summary>
    public async Task<HybridRetriever> HybridAsync(OnnxEmbeddingModel model, int perSource = 20)
    {
        var keyword = new LuceneRetriever("keyword", Chunks, ContosoAnalyzers.Identifiers());
        var dense = await DenseRetriever.CreateAsync("dense", Chunks, new OnnxEmbeddingGenerator(model), new OnnxEmbeddingGenerator(model, queries: true));
        return new HybridRetriever("hybrid (Chapter 11)", [keyword, dense], new ReciprocalRankFusion(60), perSource);
    }

    /// <summary>The cross-encoders this chapter compares, by short name.</summary>
    public static OnnxCrossEncoder CrossEncoder(string which, int maxTokens = 512) => which switch
    {
        "L6" => new("MiniLM-L6, FP32", Model("ms-marco-MiniLM-L6-v2", "model.onnx"), Vocab(), maxTokens, spinning: false),
        "L6-int8" => new("MiniLM-L6, INT8", Model("ms-marco-MiniLM-L6-v2", "model_quint8_avx2.onnx"), Vocab(), maxTokens, spinning: false),
        "L12" => new("MiniLM-L12, FP32", Model("ms-marco-MiniLM-L12-v2", "model.onnx"), Vocab(), maxTokens, spinning: false),
        _ => throw new ArgumentException($"Unknown cross-encoder '{which}'."),
    };

    private static string Model(string folder, string file) => Path.Combine(EmbeddingModels.Root, folder, file);

    private static string Vocab() => Path.Combine(EmbeddingModels.Root, "ms-marco-MiniLM-L6-v2", "vocab.txt");

    public Task<RunRecord> RunAsync(IRetriever retriever, int k = 4) => Harness.RunAsync(new Configuration(retriever, Chunks), Questions, k, Split);

    /// <summary>Median and 95th-percentile time of a whole retrieval, one answerable question at a time.</summary>
    public async Task<(double P50, double P95)> LatencyAsync(IRetriever retriever, int top = 4)
    {
        var times = new List<double>();
        foreach (var question in Answerable)
        {
            var stopwatch = Stopwatch.StartNew();
            await retriever.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, top));
            times.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return (times[times.Count / 2], times[(int)(times.Count * 0.95)]);
    }

    /// <summary>Whether a hit's return span overlaps one of the question's labeled spans.</summary>
    public bool Relevant(GoldenQuestion question, Hit hit)
    {
        var (doc, start, end) = hit.Chunk.ReturnKey;
        return question.Answerable && GoldenSet.Locate(question, Documents).Any(s => s.Doc == doc && s.Start < end && start < s.End);
    }

    public static string Signed(double x) => (Math.Round(x, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("+0.00;-0.00");

    public static string Interval(Difference d) => $"{Signed(d.Mean)} [{Signed(d.Low)}, {Signed(d.High)}]";

    public static Difference Compare(RunRecord baseline, RunRecord candidate, string metric = "Sufficient") =>
        Bootstrap.Paired([.. baseline.Answerable.Select(q => RunRecord.Value(q, metric))], [.. candidate.Answerable.Select(q => RunRecord.Value(q, metric))]);
}

/// <summary>An <see cref="IRetriever"/> as a configuration for Chapter 5's harness.</summary>
public sealed class Configuration(IRetriever retriever, IReadOnlyList<IndexedChunk> chunks) : IRetrievalUnderTest
{
    public string Name => retriever.Name;

    public IReadOnlyDictionary<string, string> Parameters { get; } = new Dictionary<string, string> { ["retriever"] = retriever.Name };

    public IReadOnlyList<ChunkSpan> Chunks { get; } = [.. chunks.Select(c => new ChunkSpan(c.Chunk.Doc, c.Chunk.ReturnStart, c.Chunk.ReturnEnd)).Distinct()];

    public async Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var result = await retriever.RetrieveAsync(new RetrievalRequest(question, asker, top), cancellationToken);
        return [.. result.Hits.Select(h => new ChunkSpan(h.Chunk.Chunk.Doc, h.Chunk.Chunk.ReturnStart, h.Chunk.Chunk.ReturnEnd))];
    }
}
