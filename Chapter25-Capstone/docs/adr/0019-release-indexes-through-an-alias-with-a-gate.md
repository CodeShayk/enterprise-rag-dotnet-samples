# ADR-0019: Release index versions through an alias behind a per-question gate

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 24

## Context

An index rebuilt in place can't be compared with the one it replaces, and a regression on one question hides inside an unchanged average.

## Decision

Index versions live side by side with manifests; the service reads an alias that a swap replaces atomically and that refuses a version embedded with another model than the query encoder. A candidate passes when the paired bootstrap's lower bound for per-question sufficiency stays above −0.03 on dev and no must-pass question (G001, G003, G004, G005, G006, G017) regresses; the test split is checked through the live alias after the swap, with automatic rollback.

## Consequences

- Rollback is one more swap, in milliseconds.
- The test split joins the release loop; fresh held-out questions must rotate in.

## Evidence

Chapter 24, `Ch24.Deployment.Cli release`, 2026-11-03: one candidate stopped with 12 regressions; another passed dev, failed G2-T005 on test and rolled back in 2.5 ms.

## Revisit when

A golden set large enough to tighten the drop, or a release cadence that makes the held-out split stale.
