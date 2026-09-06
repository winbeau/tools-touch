"""Verify install, repair, installed desktop components and uninstall in a temporary Windows directory."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import uuid
import winreg

from importlib.util import module_from_spec, spec_from_file_location

ROOT = Path(__file__).resolve().parents[1]
UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{0C8D6389-B695-4E27-9838-C559FBE45D41}_is1"


def registration():
    # Legacy installs may be registered in another registry view or scope.
    for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        for view in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
            try:
                key = winreg.OpenKey(hive, UNINSTALL_KEY, 0, winreg.KEY_READ | view)
            except FileNotFoundError:
                continue
            with key:
                try:
                    return winreg.QueryValueEx(key, "InstallLocation")[0]
                except FileNotFoundError:
                    # An incomplete registration still belongs to an existing install.
                    return ""
    return None


def run(command, cwd, timeout=300):
    process = subprocess.Popen(command, cwd=cwd)
    try:
        code = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        process.wait()
        raise RuntimeError("Installer verification timed out")
    if code != 0:
        raise RuntimeError(f"Process failed with exit code {code}: {Path(command[0]).name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--installer", required=True, type=Path)
    parser.add_argument("--previous-installer", type=Path, help="Optional previous release EXE for an actual upgrade check")
    parser.add_argument("--test-exe", required=True, type=Path, help="Self-contained Desktop.Tests executable")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if registration() is not None:
        parser.error("Tools Touch is already installed for this user; use a separate test user.")
    output = args.output.resolve()
    if output.exists():
        parser.error("Evidence output exists; choose a new directory.")
    output.mkdir(parents=True)
    spec = spec_from_file_location("native_checks", ROOT / "scripts/test-windows.py")
    native = module_from_spec(spec)
    spec.loader.exec_module(native)
    checks = []
    data_root = Path(os.environ.get("LOCALAPPDATA", tempfile.gettempdir())) / "ToolsTouch"
    data_root.mkdir(parents=True, exist_ok=True)
    data_sentinel = data_root / f"installer-check-{uuid.uuid4().hex}.json"
    legacy_database = data_root / "database" / f"installer-check-{uuid.uuid4().hex}.db"
    data_sentinel.write_text("Keep user workspace data.", encoding="utf-8")
    legacy_database.parent.mkdir(parents=True, exist_ok=True)
    legacy_database.write_bytes(b"legacy database fixture")
    with tempfile.TemporaryDirectory(prefix="ttinst-") as temporary:
        workspace = Path(temporary)
        target = workspace / "installed"
        installer = workspace / "setup.exe"
        shutil.copy2(args.installer.resolve(), installer)
        base = [str(installer), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/SP-", "/NORESTART", "/NOICONS",
                "/TASKS=", f"/DIR={target}", f"/GROUP=Tools Touch Test {uuid.uuid4().hex}"]
        try:
            if args.previous_installer:
                previous = workspace / "previous-setup.exe"
                shutil.copy2(args.previous_installer.resolve(), previous)
                run([str(previous)] + base[1:] + [f"/LOG={output / 'previous-install.log'}"], workspace)
                if Path(registration() or "").resolve() != target.resolve():
                    raise RuntimeError("Previous installer registered outside the test directory")
                upgrade_sentinel = target / "previous-version-user-file.txt"
                upgrade_sentinel.write_text("Keep across upgrades.", encoding="utf-8")
            run(base + [f"/LOG={output / 'install.log'}"], workspace)
            if Path(registration() or "").resolve() != target.resolve():
                raise RuntimeError("Per-user uninstall registration points to the wrong directory")
            checks.append("per-user installation and uninstall registration")
            if args.previous_installer:
                if upgrade_sentinel.read_text(encoding="utf-8") != "Keep across upgrades.":
                    raise RuntimeError("Upgrade removed a user-created file")
                checks.append("upgrade from previous installer preserves user-created files")
            print("PASS: per-user installation and uninstall registration", flush=True)
            manifest = json.loads((target / "BUILD-MANIFEST.json").read_text(encoding="utf-8"))
            for name, expected in manifest["files"].items():
                path = (target / name).resolve()
                if not path.is_relative_to(target) or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
                    raise RuntimeError("Installed file mismatch: " + name)
            checks.append(f"all {len(manifest['files'])} installed file hashes")
            print(f"PASS: all {len(manifest['files'])} installed file hashes", flush=True)
            sentinel = target / "user-created-file.txt"
            sentinel.write_text("Keep user-created files.", encoding="utf-8")
            # A repeat installation must restore a damaged program file without losing user files.
            (target / "settings.example.json").write_text("damaged program file", encoding="utf-8")
            run(base + [f"/LOG={output / 'repair.log'}"], workspace)
            if hashlib.sha256((target / "settings.example.json").read_bytes()).hexdigest() != manifest["files"]["settings.example.json"]:
                raise RuntimeError("Repeat installation did not repair the program file")
            if not sentinel.is_file():
                raise RuntimeError("Repeat installation deleted an untracked user file")
            if data_sentinel.read_text(encoding="utf-8") != "Keep user workspace data." or legacy_database.read_bytes() != b"legacy database fixture":
                raise RuntimeError("Repeat installation modified user workspace data")
            checks.append("repeat installation repairs files and preserves user-created files")
            print("PASS: repeat installation repair and user file preservation", flush=True)
            python = target / "python/python.exe"
            run([str(python), "-c", "import httpx, openpyxl, tools_touch_collector; print('collector-runtime-ok')"], workspace, 30)
            checks.append("bundled Python runtime imports collector and production dependencies")
            print("PASS: bundled Python runtime imports collector and production dependencies", flush=True)
            runtime = workspace / "tests"
            native.copy_tree(args.test_exe.resolve().parent, runtime)
            if (target / "config/google-client.json").is_file():
                (runtime / "config").mkdir(exist_ok=True)
                shutil.copy2(target / "config/google-client.json", runtime / "config/google-client.json")
            for name in ("ToolsTouch.dll", "ToolsTouch.Core.dll", "ToolsTouch.Application.dll", "ToolsTouch.Infrastructure.dll"):
                if (runtime / name).read_bytes() != (target / name).read_bytes():
                    raise RuntimeError("Desktop test assembly differs from installed assembly: " + name)
            run([str(runtime / "ToolsTouch.Desktop.Tests.exe"), "--output", str(output / "desktop"),
                 "--host", str(target / "agent-host/dist/index.js"), "--node", str(target / "node/node.exe")], workspace, 300)
            checks.append("installed Node/Pi and identical desktop assemblies pass native regression suite")
        finally:
            # Uninstall only if the registration still points at this exact temporary test directory.
            registered = registration()
            if registered and Path(registered).resolve() == target.resolve():
                run([str(target / "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                     f"/LOG={output / 'uninstall.log'}"], workspace)
                deadline = time.monotonic() + 30
                while (target / "unins000.exe").exists() and time.monotonic() < deadline:
                    time.sleep(0.1)
            if registration() is not None or (target / "ToolsTouch.exe").exists():
                raise RuntimeError("Uninstall did not remove installed program and registration")
        if sentinel.read_text(encoding="utf-8") != "Keep user-created files.":
            raise RuntimeError("Uninstall removed an untracked user file")
        if data_sentinel.read_text(encoding="utf-8") != "Keep user workspace data." or legacy_database.read_bytes() != b"legacy database fixture":
            raise RuntimeError("Uninstall removed user workspace data")
        checks.append("uninstall removes application and registry entry while preserving user-created files")
        data_sentinel.unlink()
        legacy_database.unlink()
        report = {"passed": True, "installerSha256": hashlib.sha256(installer.read_bytes()).hexdigest(),
                  "previousInstallerSha256": hashlib.sha256(args.previous_installer.read_bytes()).hexdigest() if args.previous_installer else None,
                  "checks": checks, "realAccountsUsed": False, "realMailSent": False}
        (output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
