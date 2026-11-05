using System.Text;
using Ch11.Hybrid.Cli;

// Commands (from the sample-code folder), on golden set v2's dev split unless --split test is given:
//   failures      where BM25 and dense retrieval each fail, by whether the question names an identifier
//   explain <id>  one question's keyword and dense rankings and their fusion
//   bm25          BM25's k1 and b swept
//   analyzers     Lucene.NET analyzers and Contoso's glossary against Chapter 9's BM25
//   filters       facets, and what a correct collection filter would buy (Chapter 13 reads filters from questions)
//   fusion        reciprocal rank fusion and convex combinations of keyword and dense rankings
//   native        hybrid search in one PostgreSQL statement (tsvector and pgvector) against fusion in the application
//   splade        learned sparse retrieval with SPLADE++ on CPU
//   pipeline      the parallel hybrid retriever: latency, time limits and degraded results
//   evaluate      the chosen hybrid against BM25 and dense retrieval, by question type
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "evaluate";
var split = args.SkipWhile(a => a != "--split").Skip(1).FirstOrDefault() ?? "dev";
var lab = await Lab.LoadAsync(split);

await (command switch
{
    "failures" => Commands.FailuresAsync(lab),
    "explain" => Commands.ExplainAsync(lab, args[1]),
    "bm25" => Commands.Bm25Async(lab),
    "analyzers" => Commands.AnalyzersAsync(lab),
    "filters" => Commands.FiltersAsync(lab),
    "fusion" => Commands.FusionAsync(lab),
    "native" => Native.RunAsync(lab),
    "splade" => Commands.SpladeAsync(lab),
    "pipeline" => Pipeline.RunAsync(lab),
    "evaluate" => Commands.EvaluateAsync(lab),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
