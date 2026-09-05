"""Build a Windows x64 portable release. No publishing, signing or real mail actions."""
import argparse
import hashlib
import json
import os
from pathlib import Path
from pathlib import PurePosixPath
import shutil
import subprocess
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NODE_VERSION = "24.16.0"
PNPM_VERSION = "10.14.0"
PYTHON_VERSION = "3.12.12"
# The workspace is locked to 3.12.12.  The last official Windows embeddable
# archive currently available for the 3.12 line is 3.12.10, so keep the
# runtime pin separate and record both values in BUILD-MANIFEST.json.
PYTHON_RUNTIME_VERSION = "3.12.10"
PYTHON_RUNTIME_ARCHIVE = f"python-{PYTHON_RUNTIME_VERSION}-embed-amd64.zip"
PYTHON_RUNTIME_URL = f"https://www.python.org/ftp/python/{PYTHON_RUNTIME_VERSION}/{PYTHON_RUNTIME_ARCHIVE}"
PYTHON_RUNTIME_SHA256 = "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3"


def run(*args, cwd=ROOT):
    subprocess.run(args, cwd=cwd, check=True)


def find_executable(name):
    candidates = (f"{name}.cmd", name) if os.name == "nt" else (name,)
    for candidate in candidates:
        found = shutil.which(candidate)
        if found:
            return found
    raise SystemExit(f"{name} required on the build machine; install the pinned development tool or add it to PATH.")


def download(url, path):
    with urllib.request.urlopen(url, timeout=120) as response, path.open("wb") as target:
        shutil.copyfileobj(response, target)


def extract_zip_safely(archive, destination):
    with zipfile.ZipFile(archive) as bundle:
        for member in bundle.infolist():
            relative = PurePosixPath(member.filename)
            if relative.is_absolute() or ".." in relative.parts or "\\" in member.filename or ":" in member.filename:
                raise SystemExit("Python runtime archive contains an unsafe path")
            if member.is_dir():
                (destination / relative).mkdir(parents=True, exist_ok=True)
                continue
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(bundle.read(member))


def prepare_python_runtime(staging, uv, temporary):
    archive = Path(temporary) / PYTHON_RUNTIME_ARCHIVE
    download(PYTHON_RUNTIME_URL, archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != PYTHON_RUNTIME_SHA256:
        raise SystemExit("Python embeddable runtime checksum mismatch")
    python_root = staging / "python"
    python_root.mkdir()
    extract_zip_safely(archive, python_root)
    pth_files = list(python_root.glob("*._pth"))
    if len(pth_files) != 1:
        raise SystemExit("Expected one Python embeddable path file")
    pth = pth_files[0]
    lines = pth.read_text(encoding="utf-8").splitlines()
    if "Lib/site-packages" not in lines:
        lines.append("Lib/site-packages")
    if "import site" not in lines:
        lines.append("import site")
    pth.write_text("\n".join(lines) + "\n", encoding="utf-8")

    wheel_directory = Path(temporary) / "collector-wheel"
    run(uv, "build", "--wheel", "--package", "tools-touch-collector", "--out-dir", str(wheel_directory),
        "--no-create-gitignore")
    wheel = next(wheel_directory.glob("tools_touch_collector-*.whl"), None)
    if wheel is None:
        raise SystemExit("Collector wheel was not built")
    site_packages = python_root / "Lib" / "site-packages"
    site_packages.mkdir(parents=True)
    run(uv, "pip", "install", "--target", str(site_packages), "--no-deps", "--link-mode", "copy", str(wheel))

    requirements = Path(temporary) / "collector-requirements.txt"
    run(uv, "export", "--package", "tools-touch-collector", "--locked", "--format", "requirements.txt",
        "--no-dev", "--no-emit-project", "--no-annotate", "--no-header", "--output-file", str(requirements))
    run(uv, "pip", "install", "--target", str(site_packages), "--python-version", "3.12",
        "--python-platform", "windows", "--only-binary", ":all:", "--require-hashes", "--link-mode", "copy",
        "--requirements", str(requirements))
    shutil.rmtree(site_packages / "bin", ignore_errors=True)

    packages = []
    for metadata in sorted(site_packages.glob("*.dist-info/METADATA")):
        values = {}
        for line in metadata.read_text(encoding="utf-8").splitlines():
            if ": " in line:
                key, value = line.split(": ", 1)
                if key in ("Name", "Version"):
                    values[key] = value
        if values.get("Name") and values.get("Version"):
            packages.append({"name": values["Name"], "version": values["Version"]})
    runtime_metadata = {
        "pythonVersion": PYTHON_RUNTIME_VERSION,
        "architecture": "amd64",
        "archive": PYTHON_RUNTIME_ARCHIVE,
        "archiveSha256": PYTHON_RUNTIME_SHA256,
        "collector": "tools-touch-collector==0.1.0",
        "packages": packages,
    }
    (python_root / "runtime.json").write_text(json.dumps(runtime_metadata, indent=2), encoding="utf-8")
    return runtime_metadata


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "ToolsTouch-win-x64")
    parser.add_argument("--google-client", type=Path, help="Publisher-provided Google Desktop OAuth client JSON to bundle")
    parser.add_argument("--public", action="store_true", help="Require bundled publisher login configuration for a public-distribution package")
    args = parser.parse_args()
    if args.public and not args.google_client:
        raise SystemExit("Public-distribution packages require --google-client; end users must not configure OAuth")
    if args.google_client:
        config = json.loads(args.google_client.read_text(encoding="utf-8-sig"))
        installed = config.get("installed", {})
        if not isinstance(installed, dict) or not str(installed.get("client_id", "")).endswith(".apps.googleusercontent.com") or not isinstance(installed.get("client_secret"), str) or not installed["client_secret"].strip():
            raise SystemExit("Google configuration must be a Desktop app OAuth client JSON")
    output = args.output.resolve()
    archive_output = Path(str(output) + ".zip")
    if output.exists() or archive_output.exists():
        raise SystemExit("Output already exists; select a new --output directory. Existing releases are never overwritten.")
    dotnet = os.environ.get("DOTNET_EXE") or find_executable("dotnet")
    pnpm = find_executable("pnpm")
    uv = find_executable("uv")
    run(pnpm, "install", "--frozen-lockfile", "--ignore-scripts")
    run(pnpm, "--filter", "tools-touch-agent-host", "test")
    run(uv, "run", "--all-packages", "--locked", "python", "-m", "unittest", "discover",
        "-s", "baoyan-cli", "-p", "test_*.py")
    run(uv, "run", "--all-packages", "--locked", "python", "-m", "unittest", "discover",
        "-s", "collector-host/tests", "-p", "test_*.py")
    run(dotnet, "run", "--project", "tests/ToolsTouch.Core.Tests")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="tools-touch-package-", dir=ROOT) as temporary:
        staging = Path(temporary) / "app"
        run(dotnet, "publish", "src/ToolsTouch.Desktop", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", str(staging))
        if args.google_client:
            (staging / "config").mkdir(exist_ok=True)
            shutil.copy2(args.google_client, staging / "config" / "google-client.json")
        host = staging / "agent-host"
        run(pnpm, "--config.node-linker=hoisted", "--filter", "tools-touch-agent-host", "deploy", "--prod", "--legacy", str(host))
        shutil.rmtree(host / "src", ignore_errors=True)
        (host / "tsconfig.json").unlink(missing_ok=True)
        shutil.rmtree(host / "dist", ignore_errors=True)
        shutil.copytree(ROOT / "agent-host" / "dist", host / "dist")
        for test in host.joinpath("dist").glob("*.test.js"):
            test.unlink()
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
        python_runtime = prepare_python_runtime(staging, uv, temporary)
        shutil.copy2(ROOT / "docs" / "WINDOWS.md", staging / "START-HERE.md")
        shutil.copy2(ROOT / "config" / "settings.example.json", staging / "settings.example.json")
        manifest = {"platform": "win-x64", "node": NODE_VERSION, "pnpm": PNPM_VERSION, "python": PYTHON_VERSION,
                    "pythonRuntime": python_runtime["pythonVersion"],
                    "pythonRuntimeArchiveSha256": python_runtime["archiveSha256"],
                    "collector": python_runtime["collector"], "pi": "0.85.0",
                    "windowsRuntimeVerified": False, "googleClientBundled": bool(args.google_client),
                    "files": {file.relative_to(staging).as_posix(): hashlib.sha256(file.read_bytes()).hexdigest()
                              for file in sorted(staging.rglob("*")) if file.is_file()}}
        (staging / "BUILD-MANIFEST.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        shutil.copytree(staging, output)
    shutil.make_archive(str(output), "zip", root_dir=output.parent, base_dir=output.name)
    print(f"Portable Windows package: {archive_output}")
    print("Build and automated tests passed. Native Windows UI, DPAPI and real account validation remain required.")


if __name__ == "__main__":
    main()
