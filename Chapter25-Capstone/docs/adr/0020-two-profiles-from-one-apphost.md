# ADR-0020: Run two deployment profiles from one AppHost; serve index versions in process at Contoso's size

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 25

## Context

Contoso needs a fully local deployment for plants without cloud access and a hybrid one where an approved cloud model writes answers. Two codebases would drift. ADR-0004 chose pgvector; Chapter 24's release design made the index version, with its vectors, the release unit.

## Decision

One Aspire AppHost starts the same API with `--Profile local` (Ollama on the CPU with every GPU hidden) or `--Profile hybrid` (an Azure OpenAI deployment named by parameters, two replicas, Redis for shared answers). Retrieval, re-ranking, the indexes and the records stay in Contoso's network in both, and in the hybrid profile evidence from a collection restricted to HR or Procurement is answered by the local model (`AI:Restricted`), as ADR-0001 requires. Index versions are served from their files in process while a version's vectors fit in memory (219 chunks, under 1 MB); past about a million chunks, or when several services share one index, the vectors move to pgvector as ADR-0004 describes.

## Consequences

- One request path to test and review; a profile is configuration.
- Amends ADR-0004 at Contoso's current size; the hybrid profile wasn't run (no cloud subscription here).
- Chapter 13's decomposition stays off: its memo of model calls is a file of questions with no retention rule, for +0.01 on compound questions; the intent check runs when a model can confirm its declines.

## Evidence

Chapter 25, `Ch25.AppHost` and `Ch25.Readiness.Cli review`, 2026-11-03.

## Revisit when

A corpus or a replica count at which loading a version's vectors per instance costs more than a shared store.
