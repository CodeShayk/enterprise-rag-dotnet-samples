# ADR-0005: Embed with bge-small-en-v1.5 (INT8) in process

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team)

## Context

The assistant needs an embedding model that runs on the CPUs it is deployed to, offline, under a license Contoso can ship, and that retrieves Contoso's evidence at least as well as BM25 does. Contoso's content is English. Chapter 3's vectors were hashes of words, a placeholder. Chapter 10 compared four real configurations on golden set v2 against Chapter 9's BM25 baseline.

## Decision

Embed chunks and queries with `BAAI/bge-small-en-v1.5` (MIT) through ONNX Runtime in the application's process, using the INT8 weights: 384 dimensions, [CLS] pooling, L2 normalization, 512 tokens, and the model card's instruction in front of every query. Record the model name on every vector. Vectors keep the 384 dimensions that Chapter 3's `ChunkRecord` stores.

## Consequences

- No embedding service to run: indexing and queries embed in process, at 42 chunks a second per core and about 96 a second on the reference laptop.
- Text is cut at 512 WordPiece tokens without an error; chunkers must stay below it (parent–child table rows of up to 600 tokens don't).
- English only. Multilingual content needs another model and a re-embedding (Chapter 10, Section 10.10).

## Evidence

`dotnet run --project Chapter10-Embeddings/Ch10.Embeddings.Cli -- retrieval --ollama` and `-- retrieval --split test --ollama`, 2026-11-01:

- Sufficient on dev and test: BM25 0.94 and 0.96; bge-small INT8 0.93 and 0.93; FP32 0.93 and 0.94; without the query instruction 0.91 and 0.92; `qwen3-embedding:0.6b` 0.95 and 0.89; hashing 0.83 and 0.81. None of the real models differs clearly from BM25.
- `throughput`: INT8 is 1.8 times as fast as FP32 on one thread; `Ch10.Tests` checks the vectors against an independent Python pipeline.

## Revisit when

Contoso adds content in other languages, a model beats this one clearly on the golden set within the CPU budget, or hybrid retrieval (Chapter 11) changes what the dense side needs to do.
