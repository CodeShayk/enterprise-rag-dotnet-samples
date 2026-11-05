# ADR-0017: Take the asker from the validated token and check every hit against live permissions

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 22

## Context

The earlier checkpoints read the asker from the request. An index lags its sources, so a revoked permission keeps working until the next update.

## Decision

The asker comes from a validated Microsoft Entra ID token (`tid`, `oid`, groups expanded through nesting, Graph on overage). Retrieval trims to what the asker may read inside the search (ADR-0002), and `VerifiedRetriever` checks every hit against the live ACLs before anything reaches a prompt, a cache or a log. Documents are screened for planted instructions at ingestion; answers are rendered through `SafeRenderer`; rate limits partition on the validated tenant.

## Consequences

- A body can't change who asks, and a revocation takes effect at the next request.
- Limits count per instance; several replicas need a shared limiter.

## Evidence

Chapter 22, `Ch22.Security.Cli`, 2026-11-03: trimming in the query returned no unreadable chunk; filtering afterward left nothing for 9 to 97 of 129 questions per person; the screen caught 4 of 4 planted documents with no false alarm on 40.

## Revisit when

A trained injection classifier that beats the rules without false alarms, or a gateway that enforces limits across replicas.
