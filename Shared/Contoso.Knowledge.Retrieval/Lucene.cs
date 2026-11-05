using System.Text.RegularExpressions;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.En;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Analysis.Pattern;
using Lucene.Net.Analysis.Synonym;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Contoso.Knowledge.Retrieval;

/// <summary>
/// Analyzers for Contoso's text. <see cref="Identifiers"/> keeps part numbers, error codes and equipment tags whole
/// and also indexes their parts and their run-together form, so "PN-4471-C", "4471" and "pn4471c" all match.
/// </summary>
public static partial class ContosoAnalyzers
{
    public const LuceneVersion Version = LuceneVersion.LUCENE_48;

    private const WordDelimiterFlags IdentifierFlags =
        WordDelimiterFlags.PRESERVE_ORIGINAL | WordDelimiterFlags.GENERATE_WORD_PARTS | WordDelimiterFlags.GENERATE_NUMBER_PARTS |
        WordDelimiterFlags.CATENATE_ALL | WordDelimiterFlags.STEM_ENGLISH_POSSESSIVE;

    /// <summary>Whitespace tokens, punctuation trimmed from their ends, split and joined at hyphens, then English stemming.</summary>
    public static Analyzer Identifiers(SynonymMap? glossary = null) => Analyzer.NewAnonymous((field, reader) =>
    {
        var source = new WhitespaceTokenizer(Version, reader);
        TokenStream stream = new PatternReplaceFilter(source, Edges(), "", true);
        stream = new WordDelimiterFilter(Version, stream, IdentifierFlags, null);
        stream = new LowerCaseFilter(Version, stream);
        if (glossary is not null) stream = new SynonymFilter(stream, glossary, true);
        stream = new StopFilter(Version, stream, EnglishAnalyzer.DefaultStopSet);
        stream = new PorterStemFilter(stream);
        return new TokenStreamComponents(source, stream);
    });

    /// <summary>A glossary in Solr's synonym format: "loto, lockout tagout" makes the two interchangeable.</summary>
    public static SynonymMap Glossary(string path)
    {
        // The glossary's own words are read with a plain analyzer, so they match the tokens they meet before stemming.
        var plain = Analyzer.NewAnonymous((field, reader) =>
        {
            var source = new WhitespaceTokenizer(Version, reader);
            return new TokenStreamComponents(source, new LowerCaseFilter(Version, source));
        });
        var parser = new SolrSynonymParser(true, true, plain);
        using var reader = File.OpenText(path);
        parser.Parse(reader);
        return parser.Build();
    }

    /// <summary>The terms an analyzer makes of a text, in order.</summary>
    public static List<string> Terms(Analyzer analyzer, string text)
    {
        using var stream = analyzer.GetTokenStream("text", text);
        var term = stream.AddAttribute<ICharTermAttribute>();
        var terms = new List<string>();
        stream.Reset();
        while (stream.IncrementToken()) terms.Add(term.ToString());
        stream.End();
        return terms;
    }

    [GeneratedRegex(@"^[\p{P}\p{S}]+|[\p{P}\p{S}]+$")]
    private static partial Regex Edges();
}

/// <summary>
/// Keyword retrieval with Lucene.NET: BM25 over an in-memory index of chunks, with permissions and metadata as filters
/// that decide what may match without changing any score.
/// </summary>
public sealed class LuceneRetriever : IRetriever, IDisposable
{
    private readonly IReadOnlyList<IndexedChunk> _chunks;
    private readonly Analyzer _queryAnalyzer;
    private readonly RAMDirectory _directory = new();
    private readonly DirectoryReader _reader;
    private readonly IndexSearcher _searcher;

    public LuceneRetriever(string name, IReadOnlyList<IndexedChunk> chunks, Analyzer analyzer, Analyzer? queryAnalyzer = null, float k1 = 1.2f, float b = 0.75f)
    {
        (Name, _chunks, _queryAnalyzer) = (name, chunks, queryAnalyzer ?? analyzer);
        var config = new IndexWriterConfig(ContosoAnalyzers.Version, analyzer) { Similarity = new BM25Similarity(k1, b) };
        using (var writer = new IndexWriter(_directory, config))
        {
            foreach (var chunk in chunks)
            {
                var document = new Document
                {
                    new StoredField("ordinal", chunk.Ordinal),
                    new TextField("text", chunk.Chunk.IndexText, Field.Store.NO),
                    new StringField("collection", chunk.Collection, Field.Store.NO),
                    new StringField("type", chunk.DocumentType, Field.Store.NO),
                    new Int32Field("from", chunk.EffectiveFrom?.DayNumber ?? int.MinValue, Field.Store.NO),
                    new Int32Field("to", chunk.EffectiveTo?.DayNumber ?? int.MaxValue, Field.Store.NO),
                };
                foreach (var group in chunk.Document.AllowedGroups) document.Add(new StringField("group", group, Field.Store.NO));
                foreach (var tag in chunk.EquipmentTags) document.Add(new StringField("equipment", tag, Field.Store.NO));
                if (chunk.EquipmentTags.Count == 0) document.Add(new StringField("equipment", RetrievalFilter.Untagged, Field.Store.NO));
                writer.AddDocument(document);
            }
        }

        _reader = DirectoryReader.Open(_directory);
        _searcher = new IndexSearcher(_reader) { Similarity = new BM25Similarity(k1, b) };
    }

    public string Name { get; }

    public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var query = new BooleanQuery();
        foreach (var term in ContosoAnalyzers.Terms(_queryAnalyzer, request.Question).Distinct())
        {
            query.Add(new TermQuery(new Term("text", term)), Occur.SHOULD);
        }

        var top = _searcher.Search(query, Filter(request), _reader.MaxDoc);
        var ranked = top.ScoreDocs.Select(d => new Hit(_chunks[_searcher.Doc(d.Doc).GetField("ordinal").GetInt32Value()!.Value], d.Score, Name));
        return Task.FromResult(RetrievalResult.Of(Ranking.DistinctReturns(ranked, request.Top)));
    }

    /// <summary>The asker's groups and the request's metadata, each a clause every hit must satisfy.</summary>
    private static QueryWrapperFilter Filter(RetrievalRequest request)
    {
        var filter = new BooleanQuery { { AnyOf("group", request.Asker.Groups), Occur.MUST } };
        if (request.Filter is { } f)
        {
            if (f.Collections is not null) filter.Add(AnyOf("collection", f.Collections), Occur.MUST);
            if (f.DocumentTypes is not null) filter.Add(AnyOf("type", f.DocumentTypes), Occur.MUST);
            if (f.EquipmentTags is not null) filter.Add(AnyOf("equipment", f.EquipmentTags), Occur.MUST);
            if (f.AsOf is { } day)
            {
                filter.Add(NumericRangeQuery.NewInt32Range("from", null, day.DayNumber, true, true), Occur.MUST);
                filter.Add(NumericRangeQuery.NewInt32Range("to", day.DayNumber, null, true, true), Occur.MUST);
            }
        }

        return new QueryWrapperFilter(filter);
    }

    private static BooleanQuery AnyOf(string field, IEnumerable<string> values)
    {
        var any = new BooleanQuery();
        foreach (var value in values) any.Add(new TermQuery(new Term(field, value)), Occur.SHOULD);
        return any;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _directory.Dispose();
    }
}
