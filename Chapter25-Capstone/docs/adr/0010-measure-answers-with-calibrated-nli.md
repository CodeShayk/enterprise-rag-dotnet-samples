# ADR-0010: Measure answers with an NLI support detector calibrated on labeled claims

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 15

## Context

Answer quality needs a measurement that runs on every build. llama3.2 as a judge agreed with the labels at kappa 0.34 to 0.43 and changed 23 of 60 pairwise verdicts when the order was swapped; Microsoft.Extensions.AI.Evaluation's built-in evaluators couldn't parse most of its replies.

## Decision

Support is decided by `cross-encoder/nli-deberta-v3-xsmall` (3-way) over the cited block's section path and text, best of the whole block and every three-sentence window, at P(entailment) ≥ 0.05, the threshold tuned on 132 labeled claims. A regression slice fails when its paired-bootstrap interval lies below zero and the mean drop exceeds 0.05.

## Consequences

- Answer checks run on the CPU in about 208 ms a claim, with no model call.
- The detector's probabilities don't transfer at 0.5; the threshold belongs to this corpus and must be retuned with new labels.

## Evidence

Chapter 15, `Ch15.Evaluation.Cli`, 2026-11-02: kappa 0.59 at 0.02 to 0.05 (AUC 0.84); judge kappa 0.34 (score ≥ 4) and 0.43 (≥ 3).

## Revisit when

A local judge that agrees with the labels better than the detector, or labels that move the threshold.
