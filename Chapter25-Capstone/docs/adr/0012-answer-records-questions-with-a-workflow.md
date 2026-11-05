# ADR-0012: Answer records questions with a fixed workflow, not an agent

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 17

## Context

Some questions need the maintenance database: counts, lists and overdue work orders. llama3.2 as a tool-calling agent chose the wrong tool, no tool, a malformed call or a wrong period too often.

## Decision

Search with the question every time; a semantic router (bge-small, written examples) decides whether a records step runs; typed lookups answer recurring questions, and guarded Text-to-SQL on DuckDB (SELECT only, the person's views, external access off, configuration locked) answers the rest, with rows checked against claims (`RowCheck`).

## Consequences

- Predictable cost and behavior: the plan is code, the model fills slots and writes one answer.
- The capstone assembles the records step (Chapter 25, check Q4): its rows join the evidence, and they stay inside Contoso's network.

## Evidence

Chapter 17, `Ch17.Agents.Cli`, 2026-11-02: dev 12/21 single pass, 14/21 workflow, 5 to 8/21 agent (−0.29 [−0.52, −0.05]); test 8/11 each.

## Revisit when

A tool-calling model on the CPU budget that plans the agent task set's dev tasks as well as the workflow.
