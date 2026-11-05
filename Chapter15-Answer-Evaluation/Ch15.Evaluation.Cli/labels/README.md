# Labels for Chapter 15

`claims.v1.jsonl` and `answers.v1.jsonl` label the answers that Chapter 14's shipped generator (setup `compressed`) gave to 60 answerable dev questions of golden set v2. The questions are drawn the way Chapter 14 drew its sample: in SHA-256 order of the question IDs. `dotnet run --project Chapter15-Answer-Evaluation/Ch15.Evaluation.Cli -- worksheet` prints the sheet they were labeled from.

They were written on 2026-11-02 by reading each claim against the evidence block it cites (the `evidence` recorded with the answer), not against the whole corpus. They haven't been double-labeled yet.

## The rules

- **supported** (one line per claim that cites a source): the cited block states the claim, or something that entails it. A true claim cited to the wrong block is unsupported, and so is an inference the block doesn't make. Claims that cite nothing aren't labeled; the deterministic checks count them.
- **correct** (one line per answer): the answer gives what the question asks, as the labeled facts state it, and contradicts none of them. Alternative spans of one fact (the same letter) count once. Claims beyond the facts are left to the claim labels, so an answer can be correct and still carry an unsupported claim.
- **note**: why, whenever the label isn't obvious from the worksheet.

## Changing a label

Don't edit a released file. Copy it to the next version (`claims.v2.jsonl`), change the line, say why in the note, and point the CLI at the new file, so that every number quoted from v1 can still be reproduced.
