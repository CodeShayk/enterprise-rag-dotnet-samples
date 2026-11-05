# ADR-0014: Build the graph by rules; use paths plus passages for multi-hop questions

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 19

## Context

Grace's supplier question needs hops the hybrid misses. llama3.2 relation extraction took 245 s for 30 chunks and added one right edge and five wrong ones.

## Decision

Rules (identifier patterns, a gazetteer, table rows through a closed schema, document-subject rules) build the graph at ingestion with provenance on every node and edge. For multi-hop questions the graph's first two passages go ahead of the re-ranked hybrid's (`PathsFirstRetriever`). Community summaries aren't wired in.

## Consequences

- Multi-hop evidence improves without a model at ingestion.
- The capstone assembles the graph route for questions that name two relations (Chapter 25, check Q4). That rule fits Chapter 19's questions; it isn't a trained router.

## Evidence

Chapter 19, `Ch19.GraphRAG.Cli`, 2026-11-03: rules 64 of 69 labeled edges at precision 0.96; multi-hop evidence 11/12 and 7/8 against 9 and 6 for the hybrid; answers 9 and 6 right against 6 and 5.

## Revisit when

Multi-hop questions in the golden set large enough to gate on, or a router that recognizes them reliably.
