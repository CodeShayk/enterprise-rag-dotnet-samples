# ADR-0013: Keep text as the page representation; cite pages by region

- Status: accepted
- Date: 2027-02-01
- Deciders: Riley Chen (Knowledge Assistant team); recorded from Chapter 18

## Context

Scanned manuals, tables and figures tempted an image-first design. CLIP page search and a vision model cost seconds to minutes a page on the CPU.

## Decision

Pages are retrieved by their text (text layer or OCR), with table runs carrying their headers and one index with modality labels by overlap; vision models run at ingestion only. A visual citation is document, page and box in points from the top left.

## Consequences

- One index and one ranking; citations a reader can open at the right region.
- Figures without words are still hard to find; golden set v2 has no figure questions.

## Evidence

Chapter 18, `Ch18.Multimodal.Cli`, 2026-11-03: the right page first for 0.82 of dev questions by text, 0.18 by CLIP, 0.51 fused.

## Revisit when

A page retriever that beats text on figure questions within the CPU budget, or a GPU in the deployment.
