using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Governance;
using Contoso.Knowledge.Retrieval;

namespace Ch23.Governance.Cli;

/// <summary>
/// What minimization costs retrieval: the shipped retriever over the indexed text as it is, with addresses and phone
/// numbers masked, and with people's names masked too, scored on the dev questions by Chapter 5's harness.
/// </summary>
public static class MinimizeCommand
{
    public static async Task RunAsync(Lab lab)
    {
        using var ner = Lab.Ner();
        var scanner = new PiiScanner(text => ner.Find(text).Where(e => e.Label == "PER" && e.Score >= 0.9).Select(e => e.Text));
        var findings = lab.Documents.Values.ToDictionary(d => d.Id, d => scanner.Scan(d.Text));
        var harness = new Harness([.. lab.Documents.Values], GoldenSet.Sha256(Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl")));
        var dev = lab.Split("dev").ToList();

        async Task<(RunRecord Run, int Changed)> Score(Func<PiiFinding, bool> masked)
        {
            var chunks = lab.Chunks.Select(c => c with { Chunk = c.Chunk with { Text = scanner.Minimize(c.Chunk.Text, [.. findings[c.Document.Id].Where(masked)]) } }).ToList();
            var changed = chunks.Zip(lab.Chunks).Count(p => p.First.Chunk.Text != p.Second.Chunk.Text);
            return (await harness.RunAsync(new Configuration(await lab.RetrieverAsync(chunks), chunks), dev, 4, "dev"), changed);
        }

        var (original, _) = await Score(_ => false);
        var (contact, contactChanged) = await Score(f => f.Kind is "email" or "phone");
        var (all, allChanged) = await Score(_ => true);
        Console.WriteLine($"Shipped retriever (hybrid, re-ranked, top 4), {dev.Count} dev questions; indexed text masked before embedding and keyword indexing");
        Console.WriteLine($"{"Indexed text",-34} {"chunks changed",14} {"sufficient",10}   against the text as it is");
        Console.WriteLine($"{"as it is",-34} {0,14} {original.Mean("Sufficient"),10:F2}");
        Console.WriteLine($"{"addresses and phone numbers masked",-34} {contactChanged,14} {contact.Mean("Sufficient"),10:F2}   {Interval(Compare(original, contact))}");
        Console.WriteLine($"{"names masked too",-34} {allChanged,14} {all.Mean("Sufficient"),10:F2}   {Interval(Compare(original, all))}");
        var lost = original.Answerable.Zip(all.Answerable).Where(p => p.First.Sufficient > p.Second.Sufficient).Select(p => p.First.Id).ToList();
        Console.WriteLine($"questions that lost their evidence with names masked: {(lost.Count == 0 ? "none" : string.Join(", ", lost))}");
    }

    private static string Signed(double x) => (Math.Round(x, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static string Interval(Difference d) => $"{Signed(d.Mean)} [{Signed(d.Low)}, {Signed(d.High)}]";

    private static Difference Compare(RunRecord baseline, RunRecord candidate) =>
        Bootstrap.Paired([.. baseline.Answerable.Select(q => RunRecord.Value(q, "Sufficient"))], [.. candidate.Answerable.Select(q => RunRecord.Value(q, "Sufficient"))]);
}

/// <summary>An <see cref="IRetriever"/> as a configuration for Chapter 5's harness, copied from Chapter 13.</summary>
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
