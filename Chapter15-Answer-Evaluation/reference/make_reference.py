"""Reference token IDs and logits for the two NLI cross-encoders, from an independent pipeline: Hugging Face `tokenizers`
and ONNX Runtime for Python. Ch15.Tests compares the C# SentencePiece tokenizer and NLI model against them.

Run from the sample-code folder after `python eng/get-models.py nli-deberta-v3-xsmall deberta-v3-xsmall-nli-binary`:
    pip install onnxruntime tokenizers numpy
    python Chapter15-Answer-Evaluation/reference/make_reference.py
"""
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "Chapter15-Answer-Evaluation" / "Ch15.Tests" / "reference" / "nli.json"
MODELS = {
    "nli-deberta-v3-xsmall": ["contradiction", "entailment", "neutral"],
    "deberta-v3-xsmall-nli-binary": ["entailment", "not_entailment"],
}

PAIRS = [
    ("Tighten the M12 flange bolts (property class 8.8, lightly oiled) to 85 N·m.", "The M12 flange bolts on P-201 take 85 N·m."),
    ("Part › Part number: PN-3311-A; Supply › Stock at Riverside: 10;", "A site should keep 10 drive-end bearings."),
    ("Error E-217 means seal-water pressure low.", "E-217 means the seal-water pressure is high."),
    ("To clear E-217, restore the seal-water pressure to at least 2.0 bar, wait until the alarm light stops flashing, "
     "then press RESET on the local panel.", "Press RESET after the seal-water pressure is back above 2 bar."),
    ("Réglage du jeu de la roue à 0,3 mm.", "The impeller clearance is 0.3 mm."),
    ("Inspect the seal faces for wear. " * 120, "The seal faces should be inspected."),
]

result = {}
for name, labels in MODELS.items():
    folder = ROOT / "models" / name
    tokenizer = Tokenizer.from_file(str(folder / "tokenizer.json"))
    tokenizer.enable_truncation(max_length=512, strategy="only_first")
    session = ort.InferenceSession(str(folder / "model.onnx"), providers=["CPUExecutionProvider"])
    rows = []
    for premise, hypothesis in PAIRS:
        encoding = tokenizer.encode(premise, hypothesis)
        ids = np.array([encoding.ids], dtype=np.int64)
        mask = np.ones_like(ids)
        logits = session.run(None, {"input_ids": ids, "attention_mask": mask})[0][0]
        rows.append({"premise": premise, "hypothesis": hypothesis, "ids": encoding.ids, "logits": [float(x) for x in logits]})
    result[name] = {"labels": labels, "pairs": rows}

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
print(f"wrote {OUT}")
for name, data in result.items():
    for row in data["pairs"]:
        probabilities = np.exp(row["logits"]) / np.exp(row["logits"]).sum()
        print(name, len(row["ids"]), dict(zip(data["labels"], np.round(probabilities, 3))), row["hypothesis"][:50])
