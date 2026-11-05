# ADR-0002: Enforce permissions inside retrieval

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team), with security review

## Context

An answer can leak a document in three ways: the document appears among the sources, the answer quotes or paraphrases it, or the answer's shape reveals that it exists. Filtering after the search fixes none of these reliably: the top results can be all restricted, leaving the asker with nothing, and results, timings and caches still depend on documents the asker can't see. Chapter 1 established the rule; Chapter 3 kept it with a filter inside the vector search.

## Decision

Every `IRetriever` returns only chunks the asker may read, and it applies the permission check before it ranks, so the top k are the asker's top k. Permissions travel with each chunk from ingestion onward. A filter applied after the search may be added as a second line of defense, never as the mechanism.

## Consequences

- Every index stores each chunk's allowed groups and every store we choose must support filtered search.
- A permission change in a source system must reach the index within a stated time, which makes permission freshness a requirement (Section 4.1) and a job for ingestion (Chapter 22 syncs permissions).
- Caches must key on the evidence or the permission scope (Chapter 3's cache keys on the evidence).
- Each new retriever adapter must pass the same contract test before it can be configured.

## Evidence

- `RetrieverContractTests.A_retriever_returns_only_chunks_the_asker_may_read` passes for the vector and keyword adapters: 25 questions, 6 people, top 20.
- Chapter 3's test with identical vectors for every text shows that the filter, not the ranking, keeps restricted chunks out.

## Revisit when

Never to weaken it. Chapter 22 extends it with identity propagation and permission sync.
