# ADR-0018: Record a hash-chained provenance record per answer, and erase through every store

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 23

## Context

Auditors and data subjects ask what produced an answer and where personal data went. Text in logs makes erasure impossible to prove.

## Decision

Every answer appends a record (pseudonym, tenant, question hash, index version, evidence with document versions and text hashes, prompt, model and digest, outcome, configuration hash) to a hash-chained log with no text in it. Every store that holds text is registered with the erasure workflow, which counts before and after and leaves a tombstone. The model and prompt registry checks every record.

## Consequences

- Erasure is provable store by store; tampering breaks the chain at the record changed.
- Stores added later must be registered, or the proof is incomplete (Chapter 25's check G5).

## Evidence

Chapter 23, `Ch23.Governance.Cli`, 2026-11-03: 996 bytes and 648 µs a record; an erasure reached nine stores, every one at zero afterward.

## Revisit when

A regulator's guidance that changes what must be kept, or storage with immutability built in.
