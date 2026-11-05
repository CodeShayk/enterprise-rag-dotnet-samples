# ADR-0001: Keep retrieval on Contoso infrastructure; choose the answer model by configuration

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team), with security and data protection review

## Context

The corpus includes restricted HR guidance, supplier contracts and three tenants whose data must not mix. Retrieval needs the full text of every document and its permissions; the answer model needs only the evidence for one question. Chapter 3 measured retrieval on a laptop CPU at a few milliseconds per question, while `llama3.2` on the same CPU took a median 8.8 seconds to its first token. Section 4.6's budget shows that no prompt with evidence meets a 3-second first-token target on this hardware.

## Decision

Ingestion, indexes, embeddings and retrieval run on Contoso infrastructure. The answer model sits behind `IChatClient`: the default is a local model, and a tenant may use an approved cloud deployment by configuration. Collections restricted to HR or Procurement never send evidence outside Contoso infrastructure, whichever model the tenant uses.

## Consequences

- Documents, indexes and embeddings stay where they are governed today; only the evidence for one answer can leave, and only for tenants that approved it.
- The answer model becomes a per-tenant configuration with an owner and a review (the Enterprise Lens in Chapter 3).
- The local first-token time misses the target until re-ranking shrinks the evidence (Chapter 12) or faster hardware arrives; the chat shows sources while the model prefills.
- Two generation paths must be tested and evaluated, not one.

## Evidence

- Chapter 3 baseline (`Ch03.Baseline.Cli`, 2026-10-31): retrieval p95 5.0 ms; `llama3.2` first token p50 8.8 s, p95 14.1 s.
- Chapter 4 budget (`Ch04.Architecture.Cli -- budget`, 2026-10-31): 131-token prompt allowance at a 3-second target.

## Revisit when

A cloud provider offers processing that legal approves for restricted content, or local inference hardware brings the first token under the target with four chunks of evidence.
