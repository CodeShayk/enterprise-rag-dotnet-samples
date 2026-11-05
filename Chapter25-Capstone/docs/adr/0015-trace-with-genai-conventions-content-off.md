# ADR-0015: Trace every stage with the GenAI conventions, content off by default

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 20

## Context

A wrong answer has to be placed: retrieval, assembly or generation. Traces that carry questions and evidence are themselves personal data.

## Decision

OpenTelemetry through the pipeline: Microsoft.Extensions.AI's `UseOpenTelemetry` for model calls and the assistant's own `rag.*` spans and metrics; content capture off by default, redacted or full only for a named debugging session; the asker as a keyed pseudonym in baggage. SLOs: answer within 15 s (95%), every citation valid (90%), evidence not weak (90%), with burn-rate alerts.

## Consequences

- Failures can be placed from traces alone for a good share of cases (13 of 34 labeled dev failures).
- Content off means some failures still need a replay with content in a controlled session.

## Evidence

Chapter 20, `Ch20.Observability.Cli`, 2026-11-03: tracing changed no answer (204 of 204 identical).

## Revisit when

A change in the GenAI conventions (still experimental) or a backend that enforces content policies itself.
