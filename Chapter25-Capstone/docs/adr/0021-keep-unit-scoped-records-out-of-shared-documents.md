# ADR-0021: Index records only with the access their system of record grants

- Status: proposed
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 25

## Context

The readiness review (check S9) found the work-order export indexed as one document readable by Fluid Systems, Motion Systems and Field Services, while the CMMS shows each unit its own assets: Nadia (Fluid Systems) read GBX-07's rows (Motion Systems) through search.

## Decision

Split the export by business unit at the connector, each part with that unit's ACL, and move golden set v2's six work-order labels to the parts in golden set v3. Until then, either leave the export out of the index (it costs the six table-row questions, Chapter 25's ablation) or accept the gap in writing.

## Consequences

- Search and the system of record agree on who sees which rows.
- Golden-set labels move with the documents, so the split ships with a new golden-set version.

## Evidence

Chapter 25, `Ch25.Readiness.Cli review` (S9) and `ablation` (the export removed), 2026-11-03.

## Revisit when

The records step (ADR-0012) serves work-order questions, after which the export needn't be indexed at all.
