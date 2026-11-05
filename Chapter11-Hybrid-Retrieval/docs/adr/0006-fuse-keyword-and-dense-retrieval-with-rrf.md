# ADR-0006: Fuse keyword and dense retrieval with RRF, behind a retriever port that carries filters

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team)

## Context

Chapter 10 found that BM25 and dense retrieval tie on golden set v2 but fail on different questions: keyword search misses paraphrases and very short questions, dense retrieval misses identifiers it splits into pieces. ADR-0003's retriever port, `RetrieveAsync(question, asker, top)`, can't express two things a combined retriever needs: metadata filters on the request, and a result that says one of its sources didn't answer. Its "Revisit when" condition has been met.

## Decision

- The retriever port becomes `IRetriever.RetrieveAsync(RetrievalRequest, CancellationToken)` returning `RetrievalResult(Hits, Degraded)`, where `RetrievalRequest(Question, Asker, Top, Filter)` carries metadata filters. Every source applies the asker's permissions and the filters inside its search, before ranking.
- The assistant retrieves with `HybridRetriever`: Lucene.NET BM25 (k1 1.2, b 0.75) with Contoso's analyzer, which keeps identifiers whole, in parts and run together, and `bge-small-en-v1.5` INT8 dense search (ADR-0005). Both run in parallel with a 200 ms limit each and return 20 parents; reciprocal rank fusion with k = 60 combines them.
- A source that fails or runs out of time is named in `Degraded`, and the search returns what the others found. The answer path decides what to tell the user (Chapter 14).

## Consequences

- The keyword index and the vector index must hold the same chunks; the ingestion pipeline writes both.
- RRF uses ranks only, so the fused score can't carry a relevance threshold; the cross-encoder of Chapter 12 supplies one.
- PostgreSQL full-text search doesn't rank the keyword side: its ranking has no inverse document frequency. Keyword search runs in Lucene.NET beside pgvector (ADR-0004).
- RRF loses a parent that only one source ranks, even first, to parents both rank moderately (question G012); re-ranking (Chapter 12) is the remedy, not a tuned weight.

## Evidence

`dotnet run --project Chapter11-Hybrid-Retrieval/Ch11.Hybrid.Cli -- evaluate` and `-- evaluate --split test`, 2026-11-01:

- Sufficient on dev and test: hybrid with RRF 0.97 and 1.00; dense alone 0.93 and 0.93 (+0.03 [+0.00, +0.07] and +0.07 [+0.01, +0.13]); Lucene alone 0.96 and 0.93; Chapter 9's BM25 0.94 and 0.96.
- A convex combination of z-scores (alpha 0.5) beat RRF on dev (nDCG@4 +0.03 [+0.02, +0.06]) and fell behind it on test (sufficient −0.03 [−0.07, +0.00]). RRF has no weight to fit to one split.
- `pipeline`: in process, running the sources in parallel costs nothing (p50 4.6 ms against 4.8 ms one after the other); a source 500 ms late costs the 200 ms limit and leaves keyword-only sufficiency (0.96).
- `native`: PostgreSQL full-text search with any of the question's words 0.70; a hybrid in one PostgreSQL statement 0.90; this hybrid 0.97.

## Revisit when

Contoso adds content in other languages (analyzers), a learned sparse model trained on maintenance text is available, or a store offers BM25 and fusion in one query that measures as well as this pair.
