"""Build a per-user Windows installer from a hash-verified portable ZIP using Inno Setup 6+."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def extended(path):
    value = str(path.resolve())
    if value.startswith("\\\\?\\"):
        return Path(value)
    return Path("\\\\?\\UNC\\" + value[2:] if value.startswith("\\\\") else "\\\\?\\" + value)


def find_compiler():
    found = os.environ.get("ISCC_EXE") or shutil.which("ISCC.exe")
    if found:
        return Path(found)
    for variable, relative in (("LOCALAPPDATA", "Programs/Inno Setup 6/ISCC.exe"),
                               ("ProgramFiles(x86)", "Inno Setup 6/ISCC.exe"),
                               ("ProgramFiles", "Inno Setup 6/ISCC.exe")):
        if variable in os.environ:
            candidate = Path(os.environ[variable]) / relative
            if candidate.is_file():
                return candidate
    raise SystemExit("Inno Setup compiler missing; install Inno Setup or set ISCC_EXE.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path, help="Portable ZIP produced by package.py")
    parser.add_argument("--version", required=True, help="Release version, for example 0.1.0")
    parser.add_argument("--output", required=True, type=Path, help="New output directory")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("Run this compiler wrapper on Windows.")
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version) or any(int(part) > 65535 for part in args.version.split(".")):
        parser.error("--version must have three numeric components, each at most 65535")
    output = args.output.resolve()
    if output.exists():
        parser.error("Output already exists; choose a new directory.")
    compiler = find_compiler()
    package = args.package.resolve()
    with tempfile.TemporaryDirectory(prefix="ttsetup-") as temporary:
        workspace = Path(temporary)
        source = workspace / "app"
        source.mkdir()
        with zipfile.ZipFile(extended(package)) as archive:
            manifests = [name for name in archive.namelist() if name.count("/") == 1 and name.endswith("/BUILD-MANIFEST.json")]
            if len(manifests) != 1:
                raise SystemExit("Expected one portable root manifest")
            manifest_name = manifests[0]
            prefix = manifest_name.rsplit("/", 1)[0] + "/"
            manifest_bytes = archive.read(manifest_name)
            manifest = json.loads(manifest_bytes)
            required = {"ToolsTouch.exe", "ToolsTouch.dll", "ToolsTouch.Core.dll",
                        "ToolsTouch.Application.dll", "ToolsTouch.Infrastructure.dll", "node/node.exe",
                        "agent-host/dist/index.js", "python/python.exe", "python/python312.dll",
                        "python/Lib/site-packages/tools_touch_collector/__main__.py"}
            if manifest.get("platform") != "win-x64" or not required.issubset(manifest["files"]):
                raise SystemExit("Incomplete Windows x64 portable package")
            # Extract only manifest-listed, verified files; do not include arbitrary local files.
            for relative, expected in manifest["files"].items():
                path = PurePosixPath(relative)
                if path.is_absolute() or ".." in path.parts or "\\" in relative or ":" in relative:
                    raise SystemExit("Invalid manifest path")
                data = archive.read(prefix + relative)
                if hashlib.sha256(data).hexdigest() != expected:
                    raise SystemExit("Package checksum mismatch: " + relative)
                target = extended(source.joinpath(*path.parts))
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            (source / "BUILD-MANIFEST.json").write_bytes(manifest_bytes)
        # Apply the same complete runtime/dependency contract as portable releases.
        subprocess.run([sys.executable, str(ROOT / "scripts/verify-package.py"), str(source)], check=True)
        script = workspace / "installer.iss"
        shutil.copy2(ROOT / "scripts/installer.iss", script)
        executable_name = f"ToolsTouch-Setup-{args.version}-win-x64.exe"
        subprocess.run([str(compiler), "/Q", f"/DAppVersion={args.version}", f"/DSourceDir={source}",
                        f"/O{workspace}", str(script)], check=True)
        output.mkdir(parents=True)
        executable = output / executable_name
        shutil.copy2(workspace / executable_name, executable)
        checksum = hashlib.sha256(executable.read_bytes()).hexdigest()
        (output / "SHA256SUMS.txt").write_text(f"{checksum}  {executable_name}\n", encoding="utf-8")
        (output / "INSTALLER-BUILD.json").write_text(json.dumps({
            "version": args.version, "installer": executable_name, "sha256": checksum,
            "portableSha256": hashlib.sha256(extended(package).read_bytes()).hexdigest(),
            "portableManifestSha256": hashlib.sha256(manifest_bytes).hexdigest(),
            "packagedFiles": len(manifest["files"]), "scope": "current-user", "signed": False
        }, indent=2), encoding="utf-8")
        print(f"Installer: {executable}")
        print(f"SHA-256: {checksum}")


if __name__ == "__main__":
    main()
