# Readiness review: Contoso Knowledge Assistant

Ch25.Knowledge.Api in process: index v1-shipped (219 chunks), model llama3.2@a80c4f17acd5, prompt grounded-f04881ce, configuration d0fb3eb74c69

**Decision:** not ready: every FAIL blocks the release (21 pass, 4 warn, 2 fail, 4 not run).

| Check | Area | Status | Evidence | Chapters | Owner |
|---|---|---|---|---|---|
| Q1 Retrieval on golden set v2 | quality | PASS | dev 0.98 (123 answerable), test 0.97 (72 answerable) on v1-shipped; the bar is 0.95 | 5, 9-12 | Knowledge Assistant team |
| Q2 Must-pass questions | quality | PASS | all 6 sufficient (G001, G003, G004, G005, G006, G017) | 24 | Knowledge Assistant team |
| Q3 Signature questions end to end | quality | PASS | G001, G003, G004, G005 and G006 answered, every claim verified, the labeled value stated; G007 and G008 declined | 14-16 | Knowledge Assistant team |
| Q4 Questions that need records or a graph | quality | WARN | records step and graph route assembled. Signature question 3: answered, rows in the evidence, count right, interval wrong. Signature question 5: answered, supplier named | 17, 19 | Knowledge Assistant team |
| Q5 Golden set coverage | quality | WARN | 204 questions, 0 about figures, 0 follow-ups: golden set v3 is due | 5, 24 | Knowledge Assistant team |
| S1 Requests without a valid token | security | PASS | no token 401, signature altered 401, signed with another key 401, another Entra tenant 403 | 22 | Knowledge Assistant team |
| S2 The asker comes from the token | security | PASS | G006 with Kwame's token and Daniel named in the body: not_found; with Daniel's token: answered | 22 | Knowledge Assistant team |
| S3 No unreadable chunk in anyone's results | security | PASS | 774 searches (6 people x 129 dev questions), 0 unreadable hits | 4, 22 | Knowledge Assistant team |
| S4 Live permissions over a stale index | security | PASS | HR-PL-2027 revoked at the source: Daniel's answer cited HR-PL-2024, PARTS-CAT-2027; restored: HR-PL-2027, HR-PL-2024, PARTS-CAT-2027 | 22 | Knowledge Assistant team |
| S5 Planted instructions quarantined at ingestion | security | PASS | 4 of 4 planted documents quarantined, 0 of 40 corpus documents | 22 | Knowledge Assistant team |
| S6 No remote images or untrusted links rendered | security | PASS | 2 removed (an image carrying data, an unknown host), the intranet link kept; 13 answers without an image or a script | 22 | Knowledge Assistant team |
| S7 Model files and digests match their records | security | PASS | 32 of 32 model files match their SHA-256; llama3.2@a80c4f17acd5 is the approved digest | 22, 23 | Knowledge Assistant team |
| S8 Rate limits per tenant across instances | security | WARN | limit 3 a minute: one instance served 3 of 4, two served 6 of 8; the hybrid profile's replicas double a tenant's limit | 21, 22 | Platform team |
| S9 Search and the system of record agree on access | security | FAIL | Nadia (fluid) reads 7 GBX-07 work-order rows (motion); the export grants fluid, motion and service, the CMMS each unit its own | 17, 22 | Maintenance systems |
| S10 HR and Procurement evidence stays inside Contoso | security | PASS | the cloud stand-in received G001 only; G006 and G017, citing HR and Procurement documents, went to the restricted model | 4, 25 | Knowledge Assistant team |
| G1 A provenance record for every answer | governance | PASS | 13 answers, 13 records, 13 with index, prompt, model and configuration d0fb3eb74c69 | 23 | Knowledge Assistant team |
| G2 The provenance chain verifies | governance | PASS | 13 records intact; record 7 changed in a copy breaks the chain at 7 | 23 | Knowledge Assistant team |
| G3 Prompt and models in the registry | governance | PASS | 0 violations; the index's embedding model is approved | 23 | Riley Chen (approver) |
| G4 Erasure reaches cached answers | governance | PASS | G017 cached on its second ask: True; SC-2024-118 erased by its tag, the next ask cached: False | 21, 23 | Knowledge Assistant team |
| G5 Stores with question text have a retention rule | governance | WARN | the escalation queue keeps the question and the asker's ID (Question, Asker, Reason, Sources, At) with no retention or erasure rule | 16, 23 | Knowledge Assistant team |
| G6 Personal data in the indexed text | governance | PASS | 0 email addresses or phone numbers in 219 chunks; 11 name Contoso staff, kept for now (Chapter 23) | 23 | Privacy office |
| O1 Healthy only with an index whose encoder matches | operability | PASS | /health Healthy; live -> v1-shipped, 219 chunks, bge-small-en-v1.5-int8 (bf64d05457cb) | 24 | Knowledge Assistant team |
| O2 Rollback is one swap | operability | PASS | swap 2.2 ms, next answer from v2-rebuild; rollback 4.0 ms, next from v1-shipped | 24 | Knowledge Assistant team |
| O3 Keyword-only when the embedding model fails | operability | PASS | embedding model failing: G003 answered, degraded: dense: HttpRequestException | 11, 24 | Knowledge Assistant team |
| O4 Sources without an answer when the model fails | operability | PASS | answer model failing: not_found, 4 sources listed instead of an answer | 16, 24 | Knowledge Assistant team |
| O5 Answer latency against the 15-second SLO | operability | FAIL | 13 answers from llama3.2@a80c4f17acd5 on the CPU: p50 19.5 s, p95 48.3 s; 3 within 15 s; 0 first-token timeouts retried | 20, 21 | Knowledge Assistant team |
| O6 A trace for every answer, without content | operability | PASS | 29 answer traces, 29 with retrieval spans, 16 with model spans; 0 spans with messages | 20 | Knowledge Assistant team |
| N1 Hybrid profile with a cloud model | operability | NOT RUN | no cloud subscription here; published as a manifest only | 25 | Platform team |
| N2 Sign-in through a Microsoft Entra ID tenant | operability | NOT RUN | Entra-shaped tokens signed with a key made for the run | 22 | Identity team |
| N3 The pipeline on a hosted runner | operability | NOT RUN | Chapter 24's workflow ran locally only | 24 | Platform team |
| N4 Load across replicas with a shared cache | operability | NOT RUN | Chapter 21's load test ran on one instance | 21 | Platform team |
