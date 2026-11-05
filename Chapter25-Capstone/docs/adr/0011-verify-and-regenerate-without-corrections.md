# ADR-0011: Ship verification, one regeneration and the abstention policy; leave corrective retrieval off

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 16

## Context

Corrective RAG grades evidence and retries retrieval. On this corpus the best parent's calibrated relevance predicted sufficient evidence at kappa 0.26 at best, and widening or rewriting never improved the facts cited beyond noise.

## Decision

`CorrectiveRag` runs with `AnswerVerifier` (Chapter 15's detector at 0.05; a citation moves only to a block that supports the claim at 0.5 or more), `FeedbackRegenerator` (once per request) and `AbstentionPolicy` with an escalation queue. No grader-driven corrections. `ModelCircuitBreaker` (3 failures, 30 s) turns a failing model into an answer that lists the sources.

## Consequences

- Invented answers are withdrawn: unanswerable test questions declined 2 of 3 against 1 of 3 for the single pass (3 of 3 took the corrective loop, which isn't shipped).
- The 95th percentile doubles when regeneration runs (dev 21.6 s to 41.3 s); NLI false alarms withdraw some correct claims, most of which regeneration gives back.

## Evidence

Chapter 16, `Ch16.Corrective.Cli compare`, 2026-11-02: verify-regenerate cited facts 0.85 on dev and 0.88 on test against 0.84 and 0.89 for the single pass (every interval touching zero) and declined 6 of 6 and 2 of 3 unanswerable questions.

## Revisit when

A grader that predicts sufficiency at kappa 0.6 or better, or a corpus where widening finds evidence the first pass missed.
