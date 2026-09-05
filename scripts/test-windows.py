"""Run isolated native WPF/DPAPI/Pi checks on Windows and save screenshots plus JSON evidence."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def copy_tree(source, destination):
    def extended(path):
        value = str(path.resolve())
        if value.startswith("\\\\?\\"):
            return value
        return "\\\\?\\UNC\\" + value[2:] if value.startswith("\\\\") else "\\\\?\\" + value
    # Nested npm dependencies can exceed the legacy Windows/UNC path limit.
    shutil.copytree(extended(source), extended(destination))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path, help="Extracted Windows portable package")
    parser.add_argument("--output", required=True, type=Path, help="New evidence directory")
    parser.add_argument("--test-exe", type=Path, help="Previously self-contained published Desktop.Tests executable")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("Native checks require Windows. Cross-compilation alone cannot run WPF or DPAPI.")
    package = args.package.resolve()
    output = args.output.resolve()
    if output.exists():
        parser.error("Evidence output already exists; choose a new directory.")
    for relative in ("agent-host/dist/index.js", "node/node.exe"):
        if not (package / relative).is_file():
            parser.error(f"Missing package file: {relative}")
    # Windows local storage avoids case-sensitive WSL shares during native DLL loading.
    with tempfile.TemporaryDirectory(prefix="ttwin-") as temporary:
        workspace = Path(temporary)
        runtime = workspace / "tests"
        if args.test_exe:
            executable = args.test_exe.resolve()
            if executable.name != "ToolsTouch.Desktop.Tests.exe" or not executable.is_file():
                parser.error("--test-exe must be the self-contained Desktop.Tests executable")
            copy_tree(executable.parent, runtime)
        else:
            subprocess.run([os.environ.get("DOTNET_EXE", "dotnet"), "publish",
                            str(ROOT / "tests/ToolsTouch.Desktop.Tests"), "-c", "Release", "-r", "win-x64",
                            "--self-contained", "true", "-o", str(runtime)], cwd=ROOT, check=True)
        for relative in ("node", "agent-host"):
            copy_tree(package / relative, workspace / relative)
        command = [str(runtime / "ToolsTouch.Desktop.Tests.exe"), "--output", str(output),
                   "--host", str(workspace / "agent-host/dist/index.js"), "--node", str(workspace / "node/node.exe")]
        process = subprocess.Popen(command, cwd=workspace)
        try:
            result = process.wait(timeout=260)
        except subprocess.TimeoutExpired:
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            process.wait()
            raise SystemExit("Native tests timed out; the test process tree was stopped.")
        print(f"Native Windows evidence: {output}")
        raise SystemExit(result)


if __name__ == "__main__":
    main()
