"""Validate the U01-U11 acceptance record without treating fixtures as live evidence."""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXPECTED = {f"U{number:02d}" for number in range(1, 12)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("matrix", type=Path, nargs="?", default=ROOT / "docs/design/real-acceptance-matrix.json")
    args = parser.parse_args()
    matrix = json.loads(args.matrix.read_text(encoding="utf-8"))
    entries = matrix.get("entries")
    if not isinstance(entries, list) or {entry.get("id") for entry in entries} != EXPECTED or len(entries) != len(EXPECTED):
        raise SystemExit("Acceptance matrix must contain exactly U01-U11 once")
    for entry in entries:
        if entry.get("status") not in {"NotVerified", "Verified"}:
            raise SystemExit(f"Invalid acceptance status: {entry.get('id')}")
        if entry.get("status") == "Verified":
            raise SystemExit(f"Real acceptance cannot be marked Verified without explicit live evidence: {entry['id']}")
        if not entry.get("automated_evidence") or not entry.get("required_real_evidence") or not entry.get("blocking_condition"):
            raise SystemExit(f"Incomplete acceptance entry: {entry['id']}")
        for relative in entry["automated_evidence"]:
            if not (ROOT / relative).exists():
                raise SystemExit(f"Missing acceptance evidence path for {entry['id']}: {relative}")
    if not matrix.get("global_not_verified"):
        raise SystemExit("Global real-acceptance gaps are required")
    print(f"PASS: {len(entries)} acceptance entries validated; all real workflow claims remain explicitly NotVerified.")


if __name__ == "__main__":
    main()
