# Readiness review: Contoso Knowledge Assistant

Ch25.Knowledge.Api in process: index v1-shipped (219 chunks), model llama3.2@a80c4f17acd5, prompt grounded-f04881ce, configuration 6279a302f5ae

**Decision:** not ready: every FAIL blocks the release (21 pass, 4 warn, 2 fail, 4 not run).

| Check | Area | Status | Evidence | Chapters | Owner |
|---|---|---|---|---|---|
| Q1 Retrieval on golden set v2 | quality | PASS | labeled facts in the top 4 on v1-shipped: dev 0.98 over 123 answerable, test 0.97 over 72 answerable; the bar is 0.95 | 5, 9-12 | Knowledge Assistant team |
| Q2 Must-pass questions | quality | PASS | all 6 sufficient (G001, G003, G004, G005, G006, G017) | 24 | Knowledge Assistant team |
| Q3 Signature questions end to end | quality | PASS | G001, G003, G004, G005 and G006 answered with every claim verified and the labeled value stated; G007 (restricted) and G008 (out of scope) declined | 14-16 | Knowledge Assistant team |
| Q4 Questions that need records or a graph | quality | WARN | the records step and the graph route aren't assembled; from the index alone, signature question 3 came back answered with 2 claims and question 5 answered with 2 | 17, 19 | Knowledge Assistant team |
| Q5 Golden set coverage | quality | WARN | 204 questions (lookup 76, numeric 74, procedural 22, temporal 16, unanswerable 9, comparative 4, multi-hop 3); 0 figure questions, 0 follow-ups: golden set v3 is due | 5, 24 | Knowledge Assistant team |
| S1 Requests without a valid token | security | PASS | no token 401, signature altered 401, signed with another key 401, another Entra tenant 403 | 22 | Knowledge Assistant team |
| S2 The asker comes from the token | security | PASS | G006 with Kwame's token and "user": "daniel.mensah" in the body: not_found; with Daniel's token: answered | 22 | Knowledge Assistant team |
| S3 No unreadable chunk in anyone's results | security | PASS | 6 people x 129 dev questions = 774 searches, 0 unreadable hits | 4, 22 | Knowledge Assistant team |
| S4 Live permissions over a stale index | security | PASS | HR-PL-2027 revoked at the source, index unchanged: Daniel's answer answered from HR-PL-2024, PARTS-CAT-2027; restored: answered from HR-PL-2027, HR-PL-2024, PARTS-CAT-2027 | 22 | Knowledge Assistant team |
| S5 Planted instructions quarantined at ingestion | security | PASS | 4 of 4 planted documents quarantined; 0 of 40 corpus documents | 22 | Knowledge Assistant team |
| S6 No remote images or untrusted links rendered | security | PASS | removed 2 (<img> https://fabrikam-support.example/p.png?d=badge-4471; link https://fabrikam-support.example/log), kept the intranet link; 13 answers so far without an image or a script | 22 | Knowledge Assistant team |
| S7 Model files and digests match their records | security | PASS | 32 of 32 model files match their SHA-256 (0 not downloaded, 0 mismatched); Ollama's llama3.2@a80c4f17acd5 is the approved digest: yes | 22, 23 | Knowledge Assistant team |
| S8 Rate limits per tenant across instances | security | WARN | limit 3 a minute for fluid: one instance served 3 of 4; two instances served 6 of 8, so the hybrid profile's two replicas give a tenant twice its limit | 21, 22 | Platform team |
| S9 Search and the system of record agree on access | security | FAIL | Nadia (fluid) reads 7 GBX-07 work-order rows (motion) through search; the export's ACL is fluid, motion and service, while the CMMS shows each unit its own assets | 17, 22 | Maintenance systems |
| S10 HR and Procurement evidence stays inside Contoso | security | PASS | the cloud stand-in received G001; G006 and G017 cited HR or Procurement documents and were answered by the restricted model | 4, 25 | Knowledge Assistant team |
| O1 Healthy only with an index whose encoder matches | operability | PASS | /health Healthy; alias live -> v1-shipped: 219 chunks, parent-child-100-600, bge-small-en-v1.5-int8 (bf64d05457cb), corpus daae152f281a | 24 | Knowledge Assistant team |
| O2 Rollback is one swap | operability | PASS | swap to v2-rebuild 3.2 ms, next answer from v2-rebuild; rollback 3.5 ms, next answer from v1-shipped | 24 | Knowledge Assistant team |
| O3 Keyword-only when the embedding model fails | operability | PASS | embedding model failing: G003 answered with 1 verified claims, degraded: dense: HttpRequestException | 11, 24 | Knowledge Assistant team |
| O4 Sources without an answer when the model fails | operability | PASS | answer model failing: not_found, "I can't write an answer right now. These documents look relevant: CP-200 Centrifugal Pump Operating and Maintenance Manual; Replacing the CP-200 mechanical seal (training); Asset record: pump P-201; Asset record: pump P-202." (4 sources) | 16, 24 | Knowledge Assistant team |
| O5 Answer latency against the 15-second SLO | operability | FAIL | 11 answers from llama3.2@a80c4f17acd5 on the CPU: p50 19.8 s, p95 44.9 s; 3 within 15 s | 20, 21 | Knowledge Assistant team |
| O6 A trace for every answer, without content | operability | PASS | 26 answer traces: 26 with retrieval and assembly spans, 13 with model and validation spans; 0 spans carrying messages | 20 | Knowledge Assistant team |
| N1 Hybrid profile with a cloud model | operability | NOT RUN | no cloud subscription here; the AppHost's hybrid profile was published as a manifest only | 25 | Platform team |
| N2 Sign-in through a Microsoft Entra ID tenant | operability | NOT RUN | tokens are shaped like Entra's and signed with a key made for the run | 22 | Identity team |
| N3 The pipeline on a hosted runner | operability | NOT RUN | Chapter 24's workflow parses and its steps run locally | 24 | Platform team |
| N4 Load across replicas with a shared cache | operability | NOT RUN | Chapter 21's closed-loop load ran on one instance | 21 | Platform team |
| G1 A provenance record for every answer | governance | PASS | 15 answers, 15 records, 15 with the index version, prompt, model and configuration 6279a302f5ae | 23 | Knowledge Assistant team |
| G2 The provenance chain verifies | governance | PASS | 15 records intact; record 8's status changed in a copy breaks the chain at 8 | 23 | Knowledge Assistant team |
| G3 Prompt and models in the registry | governance | PASS | 0 violations; the live index's embedding model sha256:bf64d05457cb is approved | 23 | Riley Chen (approver) |
| G4 Erasure reaches cached answers | governance | PASS | G017 asked twice: cached False, then True; SC-2024-118 erased from the cache by its tag, next request cached False | 21, 23 | Knowledge Assistant team |
| G5 Stores with question text have a retention rule | governance | WARN | the escalation queue keeps Question, Asker, Reason, Sources, At with no retention or erasure rule (0 escalations in this review); the provenance log keeps hashes and pseudonyms only | 16, 23 | Knowledge Assistant team |
| G6 Personal data in the indexed text | governance | PASS | 0 email addresses or phone numbers in 219 chunks; 11 chunks name Contoso staff, kept until golden set v3 has questions about who did what (Chapter 23) | 23 | Privacy office |
