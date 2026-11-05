using Contoso.Corpus;
using Contoso.Knowledge.Operations;
using Contoso.Knowledge.Retrieval;

namespace Ch24.Deployment.Cli;

/// <summary>
/// Shadow traffic without labels: the same questions through the live index and a candidate, compared by what each
/// returned. It finds where a candidate differs and how often, not whether it's better; the gate decides that.
/// </summary>
public static class ShadowCommand
{
    public static async Task RunAsync(Lab lab, string candidate)
    {
        var catalog = new IndexCatalog(Path.Combine(Lab.Runs, "releases"));
        var live = catalog.Alias("live")?.Version ?? throw new InvalidOperationException("Run release first.");
        var (served, shadow) = (await lab.LoadAsync(catalog, live), await lab.LoadAsync(catalog, candidate));
        var questions = lab.Questions;
        var (sameTop, sameSpans, sameDocuments) = (0, 0, 0);
        var differing = new List<string>();
        foreach (var q in questions)
        {
            var request = new RetrievalRequest(q.Question, People.Find(q.Asker)!, 4);
            var a = (await served.Retriever.RetrieveAsync(request)).Hits.Select(h => h.Chunk.ReturnKey).ToList();
            var b = (await shadow.Retriever.RetrieveAsync(request)).Hits.Select(h => h.Chunk.ReturnKey).ToList();
            if (a.FirstOrDefault() == b.FirstOrDefault()) sameTop++;
            if (a.Select(k => k.Doc).ToHashSet().SetEquals(b.Select(k => k.Doc))) sameDocuments++;
            if (a.ToHashSet().SetEquals(b)) sameSpans++;
            else differing.Add(q.Id);
        }

        Console.WriteLine($"Shadow: {questions.Count} questions (dev and test, labels unused) through live {live} and {candidate}, top 4");
        Console.WriteLine($"  same top span: {sameTop}; same documents in the top 4: {sameDocuments}; same spans in the top 4: {sameSpans}");
        Console.WriteLine($"  differing spans: {differing.Count} questions; the gate's G2-T004, G2-T005 and G2-T020 among them: {string.Join(", ", new[] { "G2-T004", "G2-T005", "G2-T020" }.Select(id => differing.Contains(id) ? "yes" : "no"))}");
    }
}
