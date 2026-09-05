"""Verify a generated portable directory and its isolated production runtimes."""
import hashlib
import json
from pathlib import Path
import re
import sys

root = Path(sys.argv[1]).resolve()
manifest = json.loads((root / "BUILD-MANIFEST.json").read_text(encoding="utf-8"))
required = {"ToolsTouch.exe", "ToolsTouch.dll", "ToolsTouch.Core.dll", "ToolsTouch.Application.dll",
            "ToolsTouch.Infrastructure.dll", "node/node.exe", "agent-host/package.json",
            "agent-host/dist/index.js", "agent-host/dist/tools.js",
            "agent-host/node_modules/@earendil-works/pi-coding-agent/package.json", "python/python.exe",
            "python/python312.dll", "python/python312._pth", "python/runtime.json",
            "python/Lib/site-packages/tools_touch_collector/__main__.py"}
if not required.issubset(manifest["files"]):
    raise SystemExit("Incomplete package manifest")
if manifest.get("pnpm") != "10.14.0" or manifest.get("python") != "3.12.12":
    raise SystemExit("Package was not built with the pinned pnpm/Python toolchain")
if manifest.get("pythonRuntime") != "3.12.10" or manifest.get("collector") != "tools-touch-collector==0.1.0":
    raise SystemExit("Package was not built with the pinned Windows Python runtime")
if "agent-host/package-lock.json" in manifest["files"]:
    raise SystemExit("Portable package must use the pnpm workspace lock, not a nested npm lock")
pth = (root / "python/python312._pth").read_text(encoding="utf-8")
if "Lib/site-packages" not in pth.splitlines() or "import site" not in pth.splitlines():
    raise SystemExit("Embedded Python path file does not enable the bundled site-packages")
runtime = json.loads((root / "python/runtime.json").read_text(encoding="utf-8"))
if runtime.get("archiveSha256") != manifest.get("pythonRuntimeArchiveSha256") or runtime.get("pythonVersion") != manifest.get("pythonRuntime"):
    raise SystemExit("Python runtime metadata does not match the build manifest")
normalize = lambda value: re.sub(r"[-_.]+", "-", value).lower()
if not runtime.get("packages") or {normalize(package.get("name", "")) for package in runtime["packages"]} != {
        "anyio", "certifi", "et-xmlfile", "h11", "httpcore", "httpx", "idna", "openpyxl", "tools-touch-collector", "typing-extensions"}:
    raise SystemExit("Unexpected bundled collector dependency set")
for directory in root.rglob("*"):
    if directory.is_symlink():
        raise SystemExit(f"Portable package contains a symlink: {directory.relative_to(root)}")
if (root / "agent-host/src").exists() or (root / "agent-host/tsconfig.json").exists():
    raise SystemExit("Portable AgentHost contains development sources")
if list((root / "agent-host/dist").glob("*.test.js")):
    raise SystemExit("Portable AgentHost contains test output")
listed = set(manifest["files"])
actual = {file.relative_to(root).as_posix() for file in root.rglob("*") if file.is_file() and file.name != "BUILD-MANIFEST.json"}
if actual != listed:
    extra = sorted(actual - listed)
    missing = sorted(listed - actual)
    raise SystemExit(f"Package manifest file set mismatch; extra={extra[:3]} missing={missing[:3]}")
for name, expected in manifest["files"].items():
    path = (root / name).resolve()
    if not path.is_relative_to(root) or not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        raise SystemExit(f"Package integrity mismatch: {name}")
print(f"PASS: {len(manifest['files'])} packaged file hashes and isolated Node/Python runtime contract.")
print("Native Windows WPF, DPAPI, installer lifecycle and real account validation still require Windows.")
