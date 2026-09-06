"""Native Windows regression for hash verification through DOS 8.3 aliases."""
import ctypes
import hashlib
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest

if os.name == 'nt':
    spec = importlib.util.spec_from_file_location('installer_checks', Path(__file__).with_name('test-installer.py'))
    checks = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(checks)


@unittest.skipUnless(os.name == 'nt', 'Windows paths require native Windows')
class InstallerPathTests(unittest.TestCase):
    def test_native_short_alias_resolves_to_same_installation(self):
        with tempfile.TemporaryDirectory(prefix='tools-touch-installer-path-') as directory:
            root = Path(directory)
            (root / 'Accessibility.dll').write_bytes(b'isolated runtime fixture')
            short_name = ctypes.create_unicode_buffer(32768)
            length = ctypes.windll.kernel32.GetShortPathNameW(str(root), short_name, len(short_name))
            self.assertGreater(length, 0)
            alias = Path(short_name.value)
            if str(alias).casefold() == str(root.resolve()).casefold():
                self.skipTest('This volume has no DOS 8.3 alias for the test directory')
            # Reproduce the old false negative before invoking the fixed verifier.
            self.assertFalse((alias / 'Accessibility.dll').resolve().is_relative_to(alias))
            checks.verify_installed_files(alias, {'Accessibility.dll': hashlib.sha256(b'isolated runtime fixture').hexdigest()})

    def test_hash_and_directory_boundary_checks_remain_enforced(self):
        with tempfile.TemporaryDirectory(prefix='tools-touch-installer-path-') as directory:
            root = Path(directory)
            (root / 'runtime.dll').write_bytes(b'fixture')
            with self.assertRaisesRegex(RuntimeError, 'hash mismatch'):
                checks.verify_installed_files(root, {'runtime.dll': 'incorrect hash'})
            with self.assertRaisesRegex(RuntimeError, 'escapes test directory'):
                checks.verify_installed_files(root, {'../outside-file': 'unused hash'})


if __name__ == '__main__':
    unittest.main()
