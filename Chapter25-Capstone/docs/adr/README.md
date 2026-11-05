# The assistant's decision log

Every architecture decision record from Chapters 4 to 25, in order. The files for ADR-0001 to ADR-0008 live with the chapter that made them; Chapter 25 recorded the decisions Chapters 14 to 24 made in their notes and added its own.

| ADR | Decision | Chapter | Status | File |
|---|---|---|---|---|
| 0001 | Keep retrieval on Contoso infrastructure; choose the answer model by configuration | 4 | accepted | `Chapter04-Architecture/docs/adr/0001-keep-retrieval-local-and-configure-generation.md` |
| 0002 | Enforce permissions inside retrieval | 4 | accepted | `Chapter04-Architecture/docs/adr/0002-enforce-permissions-inside-retrieval.md` |
| 0003 | Put models and stores behind ports | 4 | accepted | `Chapter04-Architecture/docs/adr/0003-put-models-and-stores-behind-ports.md` |
| 0004 | Store vectors in PostgreSQL with pgvector, tuned for filtered search | 10 | accepted; amended by ADR-0020 | `Chapter10-Embeddings/docs/adr/0004-store-vectors-in-postgresql-with-pgvector.md` |
| 0005 | Embed with bge-small-en-v1.5 (INT8) in process | 10 | accepted | `Chapter10-Embeddings/docs/adr/0005-embed-with-bge-small-int8-in-process.md` |
| 0006 | Fuse keyword and dense retrieval with RRF, behind a retriever port that carries filters | 11 | accepted; the 200 ms source limit became 2 s (Chapters 11 and 24) | `Chapter11-Hybrid-Retrieval/docs/adr/0006-fuse-keyword-and-dense-retrieval-with-rrf.md` |
| 0007 | Re-rank the hybrid's top eight with a CPU cross-encoder, mainly for a calibrated relevance score | 12 | accepted | `Chapter12-Reranking/docs/adr/0007-rerank-eight-candidates-for-a-calibrated-score.md` |
| 0008 | Plan queries with only the steps that measured well | 13 | accepted; decomposition off in the capstone (ADR-0020) | `Chapter13-Query-Understanding/docs/adr/0008-plan-queries-with-the-steps-that-measured-well.md` |
| 0009 | Answer with validated claims, and abstain before the model when nothing is relevant | 14 | accepted | `Chapter25-Capstone/docs/adr/0009-answer-with-validated-claims.md` |
| 0010 | Measure answers with an NLI support detector calibrated on labeled claims | 15 | accepted | `Chapter25-Capstone/docs/adr/0010-measure-answers-with-calibrated-nli.md` |
| 0011 | Ship verification, one regeneration and the abstention policy; leave corrective retrieval off | 16 | accepted | `Chapter25-Capstone/docs/adr/0011-verify-and-regenerate-without-corrections.md` |
| 0012 | Answer records questions with a fixed workflow, not an agent | 17 | accepted | `Chapter25-Capstone/docs/adr/0012-answer-records-questions-with-a-workflow.md` |
| 0013 | Keep text as the page representation; cite pages by region | 18 | accepted | `Chapter25-Capstone/docs/adr/0013-keep-text-as-the-page-representation.md` |
| 0014 | Build the graph by rules; use paths plus passages for multi-hop questions | 19 | accepted | `Chapter25-Capstone/docs/adr/0014-build-the-graph-by-rules-for-multi-hop.md` |
| 0015 | Trace every stage with the GenAI conventions, content off by default | 20 | accepted | `Chapter25-Capstone/docs/adr/0015-trace-with-genai-conventions-content-off.md` |
| 0016 | Cache the model's work by the evidence it reads; no semantic cache, no cascade | 21 | accepted | `Chapter25-Capstone/docs/adr/0016-cache-answers-by-evidence.md` |
| 0017 | Take the asker from the validated token and check every hit against live permissions | 22 | accepted | `Chapter25-Capstone/docs/adr/0017-identity-from-token-and-live-permission-checks.md` |
| 0018 | Record a hash-chained provenance record per answer, and erase through every store | 23 | accepted | `Chapter25-Capstone/docs/adr/0018-record-provenance-and-erase-everywhere.md` |
| 0019 | Release index versions through an alias behind a per-question gate | 24 | accepted | `Chapter25-Capstone/docs/adr/0019-release-indexes-through-an-alias-with-a-gate.md` |
| 0020 | Run two deployment profiles from one AppHost; serve index versions in process at Contoso's size | 25 | accepted | `Chapter25-Capstone/docs/adr/0020-two-profiles-from-one-apphost.md` |
| 0021 | Index records only with the access their system of record grants | 25 | proposed | `Chapter25-Capstone/docs/adr/0021-keep-unit-scoped-records-out-of-shared-documents.md` |
