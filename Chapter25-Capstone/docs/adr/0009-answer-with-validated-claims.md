# ADR-0009: Answer with validated claims, and abstain before the model when nothing is relevant

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 14

## Context

Chapter 3's free-text answers cited sources with markers the model chose, and nothing checked that a cited source said what the sentence claimed. Abstention and citations need a reply the code can check, and a CPU model needs a short prompt.

## Decision

`GroundedAnswerGenerator` (`Shared/Contoso.Knowledge.Generation`) gives the model the re-ranker's four parents, best first, compressed to the 500 o200k tokens of sentences MiniLM-L6 scores highest, and asks for claims (text, source, quote) through a JSON schema with at least one claim. Every quote is validated and mapped to its span; markers are rendered by code. When the best parent's calibrated probability is below 0.005 the answer is one fixed sentence, without the model: "I couldn't find this in the documents you can read."

## Consequences

- A reply the code can check, and a confidence the code computes (the share of claims verified).
- Structured output costs latency: a dev answer took 16.2 s at the median against 9.8 s for free text (llama3.2 on the CPU).
- The schema can't say everything: llama3.2 answered two unanswerable test questions with "not specified" and a padding claim.

## Evidence

Chapter 14, `Ch14.Generation.Cli`, 2026-11-02: labeled facts cited 0.84 against 0.75 on dev (+0.09 [+0.00, +0.17]) and 0.89 against 0.76 on test (+0.13 [+0.03, +0.24]); quotes verified 0.89 and 0.82; unanswerable declined 6/6 and 1/3.

## Revisit when

A model that writes valid claims reliably at a lower latency, or a context window that makes compression unnecessary at Contoso's budget.
