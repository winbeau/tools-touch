"""Verify a generated portable directory against its build manifest (not a code signature)."""
import hashlib
import json
from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve()
manifest = json.loads((root / "BUILD-MANIFEST.json").read_text(encoding="utf-8"))
required = {"ToolsTouch.exe", "ToolsTouch.dll", "ToolsTouch.Core.dll", "node/node.exe", "agent-host/dist/index.js",
            "agent-host/dist/tools.js", "agent-host/node_modules/@earendil-works/pi-coding-agent/package.json"}
if not required.issubset(manifest["files"]):
    raise SystemExit("Incomplete package manifest")
for name, expected in manifest["files"].items():
    path = (root / name).resolve()
    if not path.is_relative_to(root) or not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        raise SystemExit(f"Package integrity mismatch: {name}")
print(f"PASS: {len(manifest['files'])} packaged file hashes. Hash verification does not establish native Windows acceptance.")
