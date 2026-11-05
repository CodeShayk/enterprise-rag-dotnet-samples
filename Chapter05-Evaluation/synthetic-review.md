# Synthetic question review, golden set v1

`llama3.2` (Ollama 0.34.0, CPU only) drafted one question per document from its first 200-token chunk with `dotnet run --project Chapter05-Evaluation/Ch05.Evaluation.Cli -- generate` on 2026-10-31: 24 passages, 24 parseable answers, 23 quotes found verbatim, 3.0 minutes. Every candidate was reviewed. Accepted questions are G053 to G062 in `Shared/Contoso.Corpus/golden-set.v1.jsonl`, with `"source": "synthetic"`.

| Document | Draft question | Decision | Reason or edit |
|---|---|---|---|
| CP200-MAN-2009-5.4 | What should I do with the suction valve during start-up? | accepted (G053) | unchanged |
| CP200-MAN-2009-6.3 | What is the recommended tightening torque for the M12 flange bolts? | rejected | duplicates G001 |
| CP200-MAN-2009-7.2 | How often should I inspect the mechanical seal and the bearings? | rejected | duplicates G004, in the passage's own words |
| CP200-TRB-E104 | What should I do if the motor's winding temperature exceeds 155 °C? | accepted, edited (G055) | the quote said what the error means, not what to do; asked what happens instead, with the matching sentence |
| CP200-TRB-E217 | What should I do if the seal-water pressure is below 1.5 bar for 10 seconds? | rejected | the quote describes the trip, not what to do |
| CP200-TRB-VIB | What is the threshold for vibration on the bearing housing that requires immediate investigation? | rejected | duplicates G032's span; "immediate" isn't in the passage |
| CPL-RX-INST | How often should the elastomer element be replaced? | accepted, edited (G056) | the question presumed a time interval; the manual gives a condition |
| ECN-2291 | What is the reason for the change from PN-4471-B to PN-4471-C on CP-200 pumps? | accepted (G054) | unchanged |
| HG400-MAN-3.2 | What is the recommended tightening torque for the four M16 foot bolts? | rejected | duplicates G022 |
| HG400-MAN-4.1 | How often should I change the oil in the HG-400 gearbox? | rejected | duplicates G010 |
| HR-PL-2024 | How long can I take parental leave after the birth or adoption of a child? | rejected | the answer depends on the date, which the question doesn't give |
| HR-PL-2027 | What is the effective date for the new paid parental leave policy? | accepted, edited (G057) | the quote was the announcement date; corrected to the sentence with March 1, 2027 |
| IT-POL-003 | Can I use public AI chatbots for translation and customer data input? | rejected | duplicates G019 |
| P201-ASSET | What is the model of the pump? | accepted, edited (G058) | named the pump: "What model of pump is P-201?" |
| PROC-POL-007 | What is the process for a new supplier to become qualified? | rejected | duplicates G039 |
| SB-0932 | What is the procedure for opening a CP-200 seal housing? | rejected | duplicates G009 and G033 |
| SB-0947 | Do I need a hot work permit for welding, grinding, and cutting outside the workshops? | accepted, edited (G059) | repeated the passage word for word; rewritten as a technician would ask |
| SC-2024-118 | What is the duration of the contract for the supply of mechanical seals for CP-200 pumps? | rejected | duplicates G017 |
| SOP-FS-014 | Do I need to mix PN-4471-B and PN-4471-C parts together? | accepted, edited (G060) | awkward premise; rewritten as "Can I use PN-4471-B parts with a PN-4471-C seal?" |
| SR-2026-1107 | What was the cause of the CP-200 tripping four times in two days? | rejected | duplicates G016; the quote gives a symptom, not the cause |
| SR-2026-1213 | What was the recommended maintenance schedule for the HG-400 gearbox? | accepted, edited (G061) | ambiguous with the manual; tied to the Woodgrove Foods visit |
| SVC-POL-002 | What is the response time for a P1 call? | rejected automatically | the quote isn't in the passage (the table was run together) |
| VFD-MAN-F012 | What should I do if the drive stops the motor due to fault F012? | rejected | the quote says what the drive does, not what to do |
| WI-FS-031 | How often should the screen be cleaned? | accepted, edited (G062) | named the strainer and the plant |

Totals: 24 drafts; 1 rejected automatically; 13 rejected on review (10 duplicates, 2 quotes that don't answer, 1 ambiguous); 10 accepted (2 unchanged, 8 edited).
