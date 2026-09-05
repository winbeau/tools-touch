"""Build a Windows x64 portable release. No publishing, signing or real mail actions."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NODE_VERSION = "24.16.0"


def run(*args, cwd=ROOT):
    subprocess.run(args, cwd=cwd, check=True)


def download(url, path):
    with urllib.request.urlopen(url, timeout=120) as response, path.open("wb") as target:
        shutil.copyfileobj(response, target)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "ToolsTouch-win-x64")
    args = parser.parse_args()
    output = args.output.resolve()
    archive_output = Path(str(output) + ".zip")
    if output.exists() or archive_output.exists():
        raise SystemExit("Output already exists; select a new --output directory. Existing releases are never overwritten.")
    dotnet = os.environ.get("DOTNET_EXE", "dotnet")
    npm = shutil.which("npm.cmd" if os.name == "nt" else "npm")
    if not npm:
        raise SystemExit("Node.js/npm required on the build machine.")
    run(npm, "ci", "--ignore-scripts", cwd=ROOT / "agent-host")
    run(npm, "test", cwd=ROOT / "agent-host")
    run(dotnet, "run", "--project", "tests/ToolsTouch.Core.Tests")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="tools-touch-package-", dir=output.parent) as temporary:
        staging = Path(temporary) / "app"
        run(dotnet, "publish", "src/ToolsTouch.Desktop", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", str(staging))
        host = staging / "agent-host"
        host.mkdir()
        shutil.copytree(ROOT / "agent-host" / "dist", host / "dist")
        for test in (host / "dist").glob("*.test.js"):
            test.unlink()
        for name in ("package.json", "package-lock.json"):
            shutil.copy2(ROOT / "agent-host" / name, host / name)
        run(npm, "ci", "--omit=dev", "--ignore-scripts", "--os=win32", "--cpu=x64", cwd=host)
        filename = f"node-v{NODE_VERSION}-win-x64.zip"
        base = f"https://nodejs.org/dist/v{NODE_VERSION}/"
        archive = Path(temporary) / filename
        sums = Path(temporary) / "SHASUMS256.txt"
        download(base + "SHASUMS256.txt", sums)
        expected = next(line.split()[0] for line in sums.read_text().splitlines() if line.split()[-1] == filename)
        download(base + filename, archive)
        if hashlib.sha256(archive.read_bytes()).hexdigest() != expected:
            raise SystemExit("Node archive checksum mismatch")
        node_directory = staging / "node"
        node_directory.mkdir()
        with zipfile.ZipFile(archive) as bundle:
            for name in ("node.exe", "LICENSE"):
                (node_directory / name).write_bytes(bundle.read(f"node-v{NODE_VERSION}-win-x64/{name}"))
        shutil.copy2(ROOT / "docs" / "WINDOWS.md", staging / "START-HERE.md")
        shutil.copy2(ROOT / "config" / "settings.example.json", staging / "settings.example.json")
        manifest = {"platform": "win-x64", "node": NODE_VERSION, "pi": "0.85.0", "windowsRuntimeVerified": False,
                    "files": {file.relative_to(staging).as_posix(): hashlib.sha256(file.read_bytes()).hexdigest()
                              for file in sorted(staging.rglob("*")) if file.is_file()}}
        (staging / "BUILD-MANIFEST.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        shutil.copytree(staging, output)
    shutil.make_archive(str(output), "zip", root_dir=output.parent, base_dir=output.name)
    print(f"Portable Windows package: {archive_output}")
    print("Build and automated tests passed. Native Windows UI, DPAPI and real account validation remain required.")


if __name__ == "__main__":
    main()
