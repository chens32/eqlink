"""Download only the release artifacts in third_party.lock.json and verify SHA-256."""
import hashlib
import json
from pathlib import Path
import sys
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parent.parent
LOCK = json.loads((ROOT / "packaging" / "third_party.lock.json").read_text())
DEST = ROOT / "artifacts" / "downloads"


def fetch(name: str) -> None:
    item = LOCK[name]
    dest = DEST / Path(item["url"]).name
    DEST.mkdir(parents=True, exist_ok=True)
    if not dest.exists():
        request = Request(item["url"], headers={"User-Agent": "EqLink-build/1"})
        with urlopen(request, timeout=90) as response, dest.open("wb") as output:
            while block := response.read(1024 * 1024):
                output.write(block)
    digest = hashlib.sha256(dest.read_bytes()).hexdigest()
    if digest.lower() != item["sha256"].lower():
        dest.unlink(missing_ok=True)
        raise ValueError(f"hash mismatch for {name}")
    print(f"{name}: verified {dest}")


if __name__ == "__main__":
    names = sys.argv[1:] or list(LOCK)
    for key in names:
        if key not in LOCK:
            raise SystemExit(f"Unknown dependency: {key}")
        fetch(key)
