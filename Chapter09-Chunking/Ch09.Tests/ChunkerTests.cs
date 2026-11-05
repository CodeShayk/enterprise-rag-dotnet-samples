using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Documents;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ch09.Tests;

public sealed class ChunkerTests
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    private static ChunkSource Source(params Block[] blocks) => new("DOC-1", "Pump manual", new CanonicalDocument("DOC-1", "1",
        new DocumentMetadata { Title = "Pump manual", Tenant = "fluid", DocumentType = "manual", Sensitivity = "internal", AllowedGroups = ["fluid"], Owner = "Nadia Rahman" },
        new SourceReference("test", "test://doc-1"), blocks));

    private static string Words(int count, string word = "seal") => string.Join(' ', Enumerable.Repeat(word, count)) + ".";

    [Fact]
    public async Task Fixed_windows_stay_within_budget_overlap_and_never_cut_a_word()
    {
        var source = Source(new ParagraphBlock(string.Join(' ', Enumerable.Range(1, 300).Select(i => $"word{i}")) + "."));

        var chunks = await new FixedSizeChunker(Tokenizer, 50, 10).SplitAsync(source, TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.InRange(Tokenizer.CountTokens(c.Text), 1, 50));
        Assert.All(chunks.Skip(1), c => Assert.Equal(' ', source.Text[c.Start - 1]));
        Assert.All(chunks.Zip(chunks.Skip(1)), p => Assert.True(p.Second.Start < p.First.End, "windows overlap"));
    }

    [Fact]
    public async Task Recursive_splitting_keeps_sentences_whole_within_the_limit()
    {
        var text = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"Sentence {i} says that the gland nuts take 25 N·m."));
        var source = Source(new ParagraphBlock(text));

        var chunks = await new RecursiveChunker(Tokenizer, 60, 10).SplitAsync(source, TestContext.Current.CancellationToken);

        Assert.All(chunks, c => Assert.True(Tokenizer.CountTokens(c.Text) <= 60 + 10));
        Assert.All(chunks, c => Assert.EndsWith(".", c.Text));
    }

    [Fact]
    public async Task A_section_that_fits_is_one_chunk_under_its_breadcrumb()
    {
        var source = Source(new HeadingBlock(1, "6 Assembly"), new HeadingBlock(2, "6.3 Flange bolt torque"), new ParagraphBlock("Tighten the M12 flange bolts to 85 N·m."));

        var chunk = Assert.Single(await new StructureChunker(Tokenizer, 300).SplitAsync(source, TestContext.Current.CancellationToken));

        Assert.Equal("Pump manual › 6 Assembly › 6.3 Flange bolt torque", chunk.Header);
        Assert.Contains("85 N·m", chunk.Text);
    }

    [Fact]
    public async Task A_long_table_becomes_key_value_rows_whose_spans_are_the_row_lines()
    {
        var rows = new List<IReadOnlyList<string>> { new[] { "Part number", "Lead time (weeks)" } };
        rows.AddRange(Enumerable.Range(1, 60).Select(i => (IReadOnlyList<string>)[$"PN-{1000 + i}-A", $"{i % 9 + 1}"]));
        var source = Source(new HeadingBlock(1, "Catalog"), new TableBlock(rows));

        var chunks = await new StructureChunker(Tokenizer, 120).SplitAsync(source, TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks.Where(c => c.Kind == "table-rows"), c =>
        {
            Assert.StartsWith("Part number: PN-", c.Text);
            Assert.StartsWith("| PN-", source.Text[c.Start..c.End]);
        });
    }

    [Fact]
    public async Task Semantic_chunking_cuts_where_the_topic_changes()
    {
        var source = Source(new ParagraphBlock("The seal leaks. The seal drips. The seal wears."), new ParagraphBlock("The invoice is due. The invoice is paid."));

        var chunks = await new SemanticChunker(new TopicEmbedder(), Tokenizer, percentile: 0.5).SplitAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks[0].Text.Split(". "), s => Assert.Contains("seal", s));
    }

    [Fact]
    public async Task Semantic_cuts_inside_a_section_fall_between_blocks_where_the_topic_changes()
    {
        var source = Source(new HeadingBlock(1, "Pump"), new ParagraphBlock(Words(60)), new ParagraphBlock(Words(60)),
            new ParagraphBlock(Words(60, "invoice")), new ParagraphBlock(Words(60, "invoice")));

        var chunks = await new SectionSemanticChunker(new TopicEmbedder(), Tokenizer, max: 150).SplitAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.StartsWith("# Pump", chunks[0].Text);
        Assert.DoesNotContain("invoice", chunks[0].Text);
        Assert.All(chunks, c => Assert.Equal("Pump manual › Pump", c.Header));
    }

    [Fact]
    public async Task A_child_returns_its_parent_and_a_sentence_returns_its_window()
    {
        var source = Source(new HeadingBlock(1, "Seal"), new ParagraphBlock(string.Join(' ', Enumerable.Range(1, 12).Select(i => $"Step {i} is done."))));
        var ct = TestContext.Current.CancellationToken;

        var children = await new ParentChildChunker(Tokenizer, child: 15, parent: 600).SplitAsync(source, ct);
        var sentences = await new ParentChildChunker(Tokenizer, window: 1).SplitAsync(source, ct);

        Assert.True(children.Count > 1);
        Assert.All(children, c => Assert.Equal((0, source.Text.Length), (c.ReturnStart, c.ReturnEnd)));
        var third = sentences.Single(s => s.Text == "Step 3 is done.");
        Assert.Equal("Step 2 is done. Step 3 is done. Step 4 is done.", source.Text[third.ReturnStart..third.ReturnEnd]);
    }

    [Fact]
    public async Task A_summary_node_returns_its_section()
    {
        var source = Source(new HeadingBlock(1, "Seal"), new ParagraphBlock(Words(120)), new ParagraphBlock(Words(120, "gland")));

        var chunks = await new HierarchicalChunker(Tokenizer, new LeadSentences(Tokenizer), passage: 100).SplitAsync(source, TestContext.Current.CancellationToken);

        var summary = Assert.Single(chunks, c => c.Kind == "summary");
        Assert.Equal(0, summary.ReturnStart);
        Assert.True(summary.ReturnEnd > chunks.Where(c => c.Kind != "summary").Max(c => c.End) - 5);
    }

    [Fact]
    public void Entity_tags_name_Contoso_identifiers()
    {
        var tags = Enrichment.Entities("Fit PN-4471-C on P-201 under ECN-2291; E-217 trips it. See SOP-FS-014 and WO-88412.");

        Assert.Equal(["PN-4471-C", "P-201", "ECN-2291", "E-217", "SOP-FS-014", "WO-88412"], tags);
    }

    [Fact]
    public void A_list_marker_stays_with_its_item_and_a_colon_ends_no_sentence()
    {
        var text = "Start the pump. Open the valve.\n6. Check the seal. Labels: 1 Seal housing; 2 Gland nuts\nUse oiled bolts, e.g. class 8.8.";

        var sentences = Sentences.Split(text).Select(s => text[s.Start..s.End]);

        Assert.Equal(["Start the pump.", "Open the valve.", "6. Check the seal.", "Labels: 1 Seal housing; 2 Gland nuts", "Use oiled bolts, e.g. class 8.8."], sentences);
    }

    /// <summary>Two topics, two directions: seal sentences point one way and invoice sentences the other.</summary>
    private sealed class TopicEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(v.Contains("seal") ? new float[] { 1, 0.1f } : new float[] { 0.1f, 1 }))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
