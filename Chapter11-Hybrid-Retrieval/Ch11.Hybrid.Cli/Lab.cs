using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Retrieval;
using Microsoft.ML.Tokenizers;

namespace Ch11.Hybrid.Cli;

/// <summary>
/// What every experiment shares: Chapter 9's corpus (one current document per logical document, read from truth),
/// its parent–child chunks with the metadata filters apply to, golden set v2 for one split and Chapter 5's harness.
/// </summary>
public sealed partial class Lab
{
    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required IReadOnlyDictionary<string, DocumentMetadata> Metadata { get; init; }

    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }

    public required IReadOnlyList<GoldenQuestion> Questions { get; init; }

    public required Harness Harness { get; init; }

    public required string Split { get; init; }

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static async Task<Lab> LoadAsync(string split)
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
        return new Lab
        {
            Documents = documents,
            Metadata = metadata,
            Chunks = chunks,
            Questions = [.. GoldenSet.Load(path).Where(q => q.Split == split)],
            Harness = new Harness([.. documents.Values], GoldenSet.Sha256(path)),
            Split = split,
        };
    }

    public IEnumerable<GoldenQuestion> Answerable => Questions.Where(q => q.Answerable);

    /// <summary>Document types grouped into collections, as in Chapter 9.</summary>
    public static string Collection(string? documentType) => documentType switch
    {
        "manual" or "troubleshooting" => "manuals",
        "sop" or "work-instruction" or "training" => "procedures",
        "parts-catalog" or "work-orders" or "asset-record" => "records",
        "change-notice" or "service-report" or "email" => "notices and reports",
        _ => "policies and bulletins",
    };

    public static OnnxEmbeddingModel Bge() => new(EmbeddingModels.BgeSmall(int8: true));

    public Task<DenseRetriever> DenseAsync(OnnxEmbeddingModel model, string name = "bge-small INT8") =>
        DenseRetriever.CreateAsync(name, Chunks, new OnnxEmbeddingGenerator(model), new OnnxEmbeddingGenerator(model, queries: true));

    public Bm25Retriever Bm25(string name = "BM25 (Chapter 9)", double k1 = 1.2, double b = 0.75) => new(name, Chunks, null, k1, b);

    public Task<RunRecord> RunAsync(IRetriever retriever, IReadOnlyDictionary<string, RetrievalFilter>? filters = null) =>
        Harness.RunAsync(new Configuration(retriever, Chunks, filters), Questions, 4, Split);

    /// <summary>A question that names a part, code or tag: letters and digits in one token, such as PN-4471-C or e217.</summary>
    public static bool HasIdentifier(string question) => Identifier().IsMatch(question);

    /// <summary>
    /// The facts a retriever's top 4 cover, out of the question's facts, and the 1-based rank of the first relevant
    /// return span in its top 20, or null.
    /// </summary>
    public async Task<(int Found, int Facts, int? First)> EvidenceAsync(IRetriever retriever, GoldenQuestion question)
    {
        var located = GoldenSet.Locate(question, Documents);
        var result = await retriever.RetrieveAsync(new RetrievalRequest(question.Question, People.Find(question.Asker)!, 20));
        bool Covers(Hit hit, LocatedSpan s) => s.Doc == hit.Chunk.ReturnKey.Doc && s.Start < hit.Chunk.ReturnKey.End && hit.Chunk.ReturnKey.Start < s.End;
        var found = located.GroupBy(s => s.Fact).Count(fact => result.Hits.Take(4).Any(h => fact.Any(s => Covers(h, s))));
        var first = result.Hits.Select((h, i) => (h, i)).FirstOrDefault(x => located.Any(s => Covers(x.h, s)));
        return (found, located.Select(s => s.Fact).Distinct().Count(), first.h is null ? null : first.i + 1);
    }

    /// <summary>A difference that rounds to zero prints as +0.00.</summary>
    public static string Signed(double x) => (Math.Round(x, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("+0.00;-0.00");

    public static string Interval(Difference d) => $"{Signed(d.Mean)} [{Signed(d.Low)}, {Signed(d.High)}]";

    public static Difference Compare(RunRecord baseline, RunRecord candidate, string metric = "Sufficient") =>
        Bootstrap.Paired([.. baseline.Answerable.Select(q => RunRecord.Value(q, metric))], [.. candidate.Answerable.Select(q => RunRecord.Value(q, metric))]);

    [GeneratedRegex(@"\b(?=[A-Za-z-]*\d)(?=[\d-]*[A-Za-z])[A-Za-z\d]+(?:-[A-Za-z\d]+)*\b")]
    private static partial Regex Identifier();
}

/// <summary>An <see cref="IRetriever"/> as a configuration for Chapter 5's harness, with optional per-question filters.</summary>
public sealed class Configuration(IRetriever retriever, IReadOnlyList<IndexedChunk> chunks, IReadOnlyDictionary<string, RetrievalFilter>? filters = null)
    : IRetrievalUnderTest
{
    public string Name => retriever.Name;

    public IReadOnlyDictionary<string, string> Parameters { get; } = new Dictionary<string, string> { ["retriever"] = retriever.Name, ["chunks"] = chunks.Count.ToString() };

    public IReadOnlyList<ChunkSpan> Chunks { get; } = [.. chunks.Select(c => new ChunkSpan(c.Chunk.Doc, c.Chunk.ReturnStart, c.Chunk.ReturnEnd)).Distinct()];

    public async Task<IReadOnlyList<ChunkSpan>> RetrieveAsync(string question, Person asker, int top, CancellationToken cancellationToken = default)
    {
        var result = await retriever.RetrieveAsync(new RetrievalRequest(question, asker, top, filters?.GetValueOrDefault(question)), cancellationToken);
        return [.. result.Hits.Select(h => new ChunkSpan(h.Chunk.Chunk.Doc, h.Chunk.Chunk.ReturnStart, h.Chunk.Chunk.ReturnEnd))];
    }
}
