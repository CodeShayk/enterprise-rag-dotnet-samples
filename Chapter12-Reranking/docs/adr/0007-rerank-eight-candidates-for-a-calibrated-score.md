# ADR-0007: Re-rank the hybrid's top eight with a CPU cross-encoder, mainly for a calibrated relevance score

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team)

## Context

The hybrid retriever of ADR-0006 already finds sufficient evidence for 0.97 of golden set v2's dev questions and all of its test questions. Its RRF scores rank but don't measure: nothing in them says whether any candidate is relevant, so the assistant can't tell a question it can answer from one it should decline, and RRF can bury a parent that only one side ranks (G012). A cross-encoder reads the question and each candidate together and returns a score per pair, at a cost per candidate.

## Decision

- Re-rank the hybrid's first eight parents with `cross-encoder/ms-marco-MiniLM-L6-v2` (Apache-2.0, FP32) on ONNX Runtime, reading each parent's breadcrumb and text up to 512 tokens, and return the top four.
- Map its logit to a probability with Platt scaling fitted on dev, P(relevant) = sigmoid(0.474 × logit − 3.543), and pass the probability with every hit. The best candidate's probability is the "nothing relevant" signal that Chapters 14 and 16 act on; 0.1 is the starting threshold.
- Run every ONNX Runtime session in the process with intra-op spinning off (`session.intra_op.allow_spinning = 0`).
- No MMR and no per-document cap.

## Consequences

- About 18 ms per candidate on the reference laptop: 157 ms at the median and 334 ms at the 95th percentile for eight, inside the latency budget of Chapter 4.
- The cross-encoder is trained on web search (MS MARCO); on Contoso's sections it ranks no better than the hybrid, and with more candidates it promotes plausible wrong sections. Re-ranking 20 candidates lost test questions (−0.07 [−0.14, −0.01] on v2's labels).
- Relevance isn't sufficiency: the torque table scores 0.59 for an M20 bolt it doesn't list. The threshold catches questions with nothing relevant, not questions with relevant but incomplete evidence.
- Golden set v2's labels for code-table questions must be completed (F33) before ranking changes can be read reliably.

## Evidence

`dotnet run --project Chapter12-Reranking/Ch12.Reranking.Cli -- evaluate --candidates 8 [--split test] [--audited]`, `-- candidates`, `-- calibrate`, `-- diversity`, `-- llm --count 20` and `-- bench`, 2026-11-01:

- Eight candidates: sufficient on dev 0.98 against 0.97 (+0.01 [−0.02, +0.04]) and on test 0.97 against 1.00 (−0.03 [−0.07, +0.00]) on v2's labels; with the labels audited to accept the paragraph that explains an error code, +0.01 [+0.00, +0.02] on dev and +0.00 on test.
- Calibration on test: expected calibration error 0.013. At P ≥ 0.1 on the best candidate, 171 of 195 answerable questions clear the threshold and 7 of the 9 that should end in "I don't know" don't.
- MMR cost sufficiency at every setting (λ 0.8: −0.03 [−0.07, −0.01]); a cap of two parents per document −0.04 [−0.08, −0.01].
- `llama3.2` as a pointwise re-ranker matched the cross-encoder on 20 dev questions at 25.4 s a question against 0.21 s.
- Two sessions with spinning on: re-ranking took 953 ms for 20 candidates; with spinning off, 351 ms.

## Revisit when

A cross-encoder fine-tuned on Contoso's question–section pairs is available, the golden set gains paraphrased and multi-section questions, or the threshold's effect on answers is measured (Chapters 14 and 16).
