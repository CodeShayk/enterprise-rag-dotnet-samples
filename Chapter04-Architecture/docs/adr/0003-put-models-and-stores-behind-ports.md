# ADR-0003: Put models and stores behind ports

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team)

## Context

The parts of a RAG system change faster than its shape. In two years the .NET model abstraction renamed its core API, the vector-store connectors moved to new packages, and most of the techniques in this book replace one stage at a time. Every replacement has to be measured against the current one, which only works if both can run behind the same seam.

## Decision

The core (`Ch04.Knowledge.Core`) holds the domain, the ports (`IDocumentSource`, `IChunker`, `IIndexWriter`, `IRetriever`, `IReranker`, `IAnswerGenerator`) and the two pipelines, and it references no SDK. Adapters implement the ports with Microsoft.Extensions.AI, Microsoft.Extensions.VectorData and anything else; the composition root picks adapters from configuration. Architecture tests enforce the boundary.

## Consequences

- Swapping a retriever, re-ranker or model is a configuration change that the evaluation harness (Chapter 5) can compare.
- One more layer of interfaces, and a contract test per port that every adapter must pass.
- A port with the wrong shape is expensive to change; hybrid retrieval (Chapter 11) is the first test of `IRetriever`.

## Evidence

- `ArchitectureTests` (four tests) pass: the core uses only the base library and the domain, its project file has no packages, every port has an adapter and adapters don't reference a host.
- `Ch04.Architecture.Cli -- ask ... --Pipeline:Retriever=Keyword` swaps the retriever with no code change.

## Revisit when

A port needs a second implementation that its signature can't express.
