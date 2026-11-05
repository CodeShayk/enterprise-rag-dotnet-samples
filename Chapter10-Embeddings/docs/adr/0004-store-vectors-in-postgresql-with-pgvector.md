# ADR-0004: Store vectors in PostgreSQL with pgvector, tuned for filtered search

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team)

## Context

Every query the assistant runs carries a permission filter (ADR-0002), and many carry metadata filters as well, so the vector store is chosen on filtered recall first and on unfiltered speed second. Contoso's collections are small by vector-store standards, hundreds of thousands of chunks at most, and the team already operates PostgreSQL for metadata, versions and lineage. The candidates measured in Chapter 10 were PostgreSQL with pgvector and Qdrant, both behind Microsoft.Extensions.VectorData, on 100,000 synthetic log entries with bge-small-en-v1.5 vectors.

## Decision

Store chunk vectors in PostgreSQL with pgvector, next to their metadata. Build HNSW with `m = 16` and `ef_construction = 200`, search with `hnsw.ef_search = 160`, and set `hnsw.iterative_scan = relaxed_order` for every query that has a filter. Keep Qdrant available behind the same `VectorStore` abstraction for collections that need exact filtered recall at any selectivity.

## Consequences

- One database to operate, back up and secure; permission and metadata filters are plain SQL that the planner can also answer with ordinary indexes when they are very selective.
- pgvector's defaults are not enough: the default build (`ef_construction = 64`) and post-filtering lose recall that the team must buy back with configuration and keep checking.
- Containers that build HNSW in parallel need more shared memory than Docker's 64 MB default.

## Evidence

`dotnet run --project Chapter10-Embeddings/Ch10.Embeddings.Cli -- indexes`, 2026-11-04, reference laptop:

- pgvector HNSW at `ef_construction` 64: recall@10 0.846 at `ef_search` 40, 0.953 at 160; at `ef_construction` 200: 0.922 and 0.981 (p50 2.7 ms). Qdrant HNSW: 0.995 at `hnsw_ef` 40, on each of its 8 segments.
- Filters matching 2.5% to 20% of rows: pgvector without iterative scans returned 1.4 to 5.2 of 10 rows (recall 0.13 to 0.48); with them, 10 rows at recall 0.86 to 0.94; Qdrant 1.000.
- `stores`: the same records, filter and query return identical results from both stores.

## Revisit when

A collection grows past what one PostgreSQL instance serves within the latency budget, or the golden set shows answers lost to filtered recall.
