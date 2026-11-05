# ADR-0016: Cache the model's work by the evidence it reads; no semantic cache, no cascade

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 21

## Context

A CPU answer costs about 163 CPU-seconds. A cache keyed by question text can serve one person's answer to another or a stale answer after a re-index.

## Decision

Retrieval runs for every request under the asker's permissions; the answer is cached on HybridCache under the prompt version, the model, the normalized question and a fingerprint of every evidence segment, with a tag per document for invalidation. No semantic cache and no 1B-to-3B cascade.

## Consequences

- Permission-safe by construction, and re-indexed or erased text changes or removes the key.
- Tag invalidation reaches the current node and Redis, not other nodes' memory until local expiry.

## Evidence

Chapter 21, `Ch21.Performance.Cli`, 2026-11-03: no semantic threshold kept near misses out on 32 pairs; the cascade lost 0.10 [−0.18, −0.04] of key values.

## Revisit when

Production logs that show paraphrase traffic a guarded semantic cache could serve, or a small model that states values reliably.
