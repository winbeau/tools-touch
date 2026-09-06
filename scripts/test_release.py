"""Regression checks for release gates that must fail before GitHub mutations."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('release', Path(__file__).with_name('release.py'))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseGateTests(unittest.TestCase):
    def test_only_plain_numeric_release_tags_are_accepted(self):
        for tag in ('main', 'v1.2.3-rc1', 'v1.2.3;echo hi', 'v01.2.3', 'v1.2'):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release.version_tuple(tag)
        self.assertLess(release.version_tuple('v0.3.9'), release.version_tuple('v0.3.10'))

    def fixture(self, directory):
        folder = Path(directory)
        release.write(folder / 'RELEASE-BUILD.json', {'sourceCommit': 'commit-a', 'version': '0.3.2'})
        (folder / 'installer.exe').write_bytes(b'isolated installer fixture')
        (folder / 'SHA256SUMS.txt').write_text(''.join(
            f'{release.sha(path)}  {path.name}\n' for path in sorted(folder.iterdir())), encoding='utf-8')
        return folder

    def run_guard(self, folder, git_result='commit-a', releases='[[]]'):
        def command(*args):
            return git_result if args[0] == 'git' else releases
        with patch.dict(os.environ, RELEASE_TAG='v0.3.2', GITHUB_REPOSITORY='example/repo', RELEASE_BUILD_RUN_ID=''), \
                patch.object(release, 'RELEASE', folder), patch.object(release, 'command', side_effect=command), \
                patch.object(release.subprocess, 'run') as mutation:
            with self.assertRaises(ValueError):
                release.publish()
            mutation.assert_not_called()

    def test_changed_tag_cannot_publish_built_assets(self):
        with tempfile.TemporaryDirectory() as directory:
            self.run_guard(self.fixture(directory), git_result='commit-b')

    def test_tampered_asset_cannot_be_uploaded(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = self.fixture(directory)
            (folder / 'installer.exe').write_bytes(b'changed bytes')
            self.run_guard(folder)

    def test_extra_file_cannot_be_accidentally_uploaded(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = self.fixture(directory)
            (folder / 'unlisted.json').write_text('{}')
            self.run_guard(folder)

    def test_published_release_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            self.run_guard(self.fixture(directory), releases=json.dumps([[{'tag_name': 'v0.3.2', 'draft': False}]]))

    def test_draft_is_found_by_listing_and_published_only_after_remote_hash_checks(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = self.fixture(directory)
            draft = {'id': 123, 'tag_name': 'v0.3.2', 'draft': True}
            assets = [{'name': p.name, 'size': p.stat().st_size, 'digest': 'sha256:' + release.sha(p)} for p in folder.iterdir()]
            calls = []
            def api(endpoint, *args):
                calls.append((endpoint, args))
                if '/assets?' in endpoint:
                    return assets
                if '/tags/' in endpoint:
                    self.assertTrue(any('--method' in arguments for _, arguments in calls))
                    return {'draft': False, 'prerelease': False, 'html_url': 'https://example.test/release'}
                self.assertIn('draft=false', args)
                self.assertIn('prerelease=false', args)
                return {}
            with patch.dict(os.environ, RELEASE_TAG='v0.3.2', GITHUB_REPOSITORY='example/repo', RELEASE_BUILD_RUN_ID=''), \
                    patch.object(release, 'RELEASE', folder), patch.object(release, 'command', return_value='commit-a'), \
                    patch.object(release, 'releases', return_value=[draft]), patch.object(release, 'api', side_effect=api), \
                    patch.object(release.subprocess, 'run'):
                release.publish()
                calls.clear()
                assets[0]['digest'] = 'sha256:tampered'
                with self.assertRaisesRegex(ValueError, 'Remote asset SHA-256'):
                    release.publish()
                self.assertFalse(any('--method' in arguments for _, arguments in calls))


if __name__ == '__main__':
    unittest.main()
