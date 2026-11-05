using Contoso.Corpus;

namespace Ch02.Fundamentals;

/// <summary>The <c>index</c> command: BM25 over Contoso's seed documents, with and without stemming.</summary>
internal static class IndexDemo
{
    private const string Paraphrase = "How tight should the screws on the P-201 pump flanges be?";

    public static void Run()
    {
        var plain = InvertedIndex.Create(SeedDocuments.All);
        var stemmed = InvertedIndex.Create(SeedDocuments.All, stem: true);
        var tomas = People.Tomas;

        Console.WriteLine($"{SeedDocuments.All.Count} documents indexed; {tomas.Name} may read {SeedDocuments.All.Count(tomas.CanRead)}.");
        Console.WriteLine("IDF: " + string.Join(", ", new[] { "m12", "torque", "pump", "seal" }.Select(t => $"{t} {plain.Idf(t):F2}")));

        Show(plain, "torque for M12 flange bolts", "no stemming", tomas);
        Show(plain, Paraphrase, "no stemming", tomas);
        Show(stemmed, Paraphrase, "stemming", tomas);
        Show(stemmed, "Which mechanical seal should I fit on P-201 now?", "stemming", tomas);
    }

    private static void Show(InvertedIndex index, string query, string label, Person asker)
    {
        Console.WriteLine($"\n{query} ({label})");
        var rank = 0;
        foreach (var hit in index.Search(query, asker))
        {
            Console.WriteLine($"  {++rank}. {hit.Document.Id,-20} {hit.Score,5:F2}  matched: {string.Join(", ", hit.MatchedTerms)}");
        }
    }
}
