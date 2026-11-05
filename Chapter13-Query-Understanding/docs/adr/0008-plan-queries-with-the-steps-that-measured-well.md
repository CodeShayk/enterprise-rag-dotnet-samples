# ADR-0008: Plan queries with only the steps that measured well

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team)

## Context

Chapter 11's hybrid retriever finds sufficient evidence for 0.97 of golden set v2's dev questions and all of its test questions, so most questions need nothing done to them before retrieval. Some do: follow-ups that lean on the previous turn, questions that ask for two things, requests that shouldn't be answered at all and small talk. Every step that changes a question can also break it, and every LLM call costs one to four seconds on the reference laptop's CPU.

## Decision

`QueryPlanner` (`Shared/Contoso.Knowledge.Query`) runs, in order:

- An intent check: the nearest of 44 written examples, embedded with bge-small, including in-scope examples paired with each unsafe one; every decline is confirmed by `llama3.2`. Only unsafe requests and chit-chat are declined; out-of-scope questions go on to retrieval, where Chapter 12's threshold decides.
- For a follow-up, the previous turn joined in front of it, without an LLM.
- For a compound question ("…, and …?"), decomposition by `llama3.2` into separate questions, retrieved separately and fused with RRF.
- A semantic route kept as a flag for Chapter 17's database tools; it never stops retrieval.

Spelling correction, rewriting, multi-query generation, HyDE and filters read from the question are left out. Filters a user chooses explicitly (facets) still apply.

## Consequences

- An LLM call for about a third of the questions (the compound ones and the declines to confirm): 15 ms at the median, 1.6 s at the 95th percentile on the reference laptop.
- A question about a rule can look like a request to break it: G2-H041 ("Can I paste a Contoso drawing into a public AI chatbot?") is declined as unsafe. Unsafe verdicts need review in the evaluation set (Chapter 15).
- Equipment filters need Contoso's metadata to say which documents apply to which asset; until it does, extracted filters are shown as suggestions, not applied.

## Evidence

`dotnet run --project Chapter13-Query-Understanding/Ch13.Query.Cli -- evaluate [--split test]`, `-- condense [--split test]`, `-- transform`, `-- filters --llm`, `-- intent`, `-- normalize` and `-- route`, 2026-11-01:

- Kept planner: dev 0.97 (+0.00 [−0.02, +0.02]), test 1.00 (+0.00). Every step as first built: dev 0.72 (−0.24 [−0.33, −0.16]), 32 answerable questions declined.
- Follow-ups: alone 0.64 (dev) and 0.50 (test); joined to the previous turn 1.00 and 0.83; condensed by `llama3.2` the same, at about 2 s and with invented details.
- Decomposing compound questions: +0.01 [+0.00, +0.02]; decomposing every question −0.03; multi-query −0.04; rewriting −0.01; HyDE +0.00 at 4.4 s a question.
- Filters: rules −0.02 to −0.04, `llama3.2` −0.05 [−0.09, −0.02]. Intent with the LLM confirming declines: 127 of 129 dev questions classified right.

## Revisit when

The golden set gains conversational, misspelled or user-worded questions (Chapter 15), Contoso's metadata links documents to assets, or a faster local model brings an LLM call under a few hundred milliseconds.
