"""Downloads the models listed in models/models.json and checks their SHA-256 hashes.

Run from the sample-code folder:  python eng/get-models.py [name ...]
Files land at models/<path>. A file that is already present with the right hash is skipped;
one with the wrong hash is downloaded again. With no names, every model in the list is fetched.
"""
import hashlib
import json
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MODELS = ROOT / "models"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main(names: list[str]) -> int:
    catalog = json.loads((MODELS / "models.json").read_text(encoding="utf-8"))
    wanted = [m for m in catalog["models"] if not names or m["name"] in names]
    unknown = set(names) - {m["name"] for m in wanted}
    if unknown:
        print(f"Unknown model(s): {', '.join(sorted(unknown))}")
        return 2
    failures = 0
    for m in wanted:
        target = MODELS / m["path"]
        if target.exists() and sha256(target) == m["sha256"]:
            print(f"ok       {m['name']:24} {m['path']}")
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        partial = target.with_suffix(target.suffix + ".part")
        print(f"fetching {m['name']:24} {m['url']}")
        with urllib.request.urlopen(m["url"], timeout=120) as response, partial.open("wb") as out:
            while block := response.read(1 << 20):
                out.write(block)
        actual = sha256(partial)
        if actual != m["sha256"]:
            partial.unlink()
            print(f"FAILED   {m['name']:24} SHA-256 {actual} doesn't match the catalog")
            failures += 1
            continue
        partial.replace(target)
        print(f"ok       {m['name']:24} {m['path']} ({target.stat().st_size / 1e6:.1f} MB, {m['license']})")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
