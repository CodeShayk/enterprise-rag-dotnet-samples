"""Reference embeddings for bge-small-en-v1.5 from an independent pipeline: Hugging Face `tokenizers` and ONNX
Runtime for Python, [CLS] pooling and L2 normalization. Ch10.Tests compares the C# generator against them.

Run from the sample-code folder after `python eng/get-models.py bge-small-en-v1.5-fp32 bge-small-en-v1.5-tokenizer`:
    pip install onnxruntime tokenizers numpy
    python Chapter10-Embeddings/reference/make_reference.py
"""
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

ROOT = Path(__file__).resolve().parents[2]
MODEL = ROOT / "models" / "bge-small-en-v1.5"
OUT = ROOT / "Chapter10-Embeddings" / "Ch10.Tests" / "reference" / "bge-small-en-v1.5.json"
INSTRUCTION = "Represent this sentence for searching relevant passages: "

TEXTS = [
    INSTRUCTION + "What torque applies to the M12 flange bolts on P-201?",
    "Tighten the M12 flange bolts (property class 8.8, lightly oiled) to 85 N·m.",
    "PN-4471-C replaces PN-4471-B under ECN-2291.",
    "Error E-217: seal-water pressure low. The pump trips.",
    "Réglage du jeu de la roue à 0,3 mm.",
    "Inspect the seal faces for wear. " * 120,
]

tokenizer = Tokenizer.from_file(str(MODEL / "tokenizer.json"))
tokenizer.enable_truncation(max_length=512)
session = ort.InferenceSession(str(MODEL / "model.onnx"))

entries = []
for text in TEXTS:
    encoding = tokenizer.encode(text)
    ids = np.array([encoding.ids], dtype=np.int64)
    feeds = {"input_ids": ids, "attention_mask": np.ones_like(ids), "token_type_ids": np.zeros_like(ids)}
    hidden = session.run(None, feeds)[0]
    vector = hidden[0, 0] / np.linalg.norm(hidden[0, 0])
    entries.append({"text": text, "tokens": encoding.ids, "vector": [round(float(x), 7) for x in vector]})

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(json.dumps({"model": "bge-small-en-v1.5 (onnx/model.onnx)", "entries": entries}, ensure_ascii=False), encoding="utf-8")
print(f"{len(entries)} reference embeddings written to {OUT.relative_to(ROOT)}")
