using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Evaluation;

namespace Ch09.Tests;

/// <summary>Golden set v2 against the generated corpus: labels that can't be located would score as misses.</summary>
public sealed class GoldenSetTests
{
    [Fact]
    public async Task Every_span_in_golden_set_v2_is_found_and_readable_by_its_asker()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", "golden-set.v2.jsonl");
        var documents = new Dictionary<string, SourceDocument>(StringComparer.Ordinal);
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null))
        {
            if (entry.Id == "SOP-FS-014" && entry.Version != "3") continue;
            var truth = await GeneratedCorpus.TruthAsync(entry, TestContext.Current.CancellationToken);
            documents[entry.Id] = new SourceDocument(entry.Id, truth.Metadata.Title, truth.Metadata.Tenant, truth.Metadata.AllowedGroups, truth.ToText().Text);
        }

        var questions = GoldenSet.Load(path);

        Assert.True(questions.Count(q => q.Answerable) >= 190, "about 200 answerable questions");
        Assert.All(questions.Where(q => q.Answerable), q => Assert.Equal(q.Relevant.Count, GoldenSet.Locate(q, documents).Count));
    }
}
