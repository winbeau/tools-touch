"""GitHub Actions release gates: immutable tags, verified assets, formal releases."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / 'artifacts'
RELEASE = ARTIFACTS / 'release'


def command(*args):
    return subprocess.check_output(args, cwd=ROOT, text=True, encoding='utf-8').strip()


def api(endpoint, *args):
    return json.loads(command('gh', 'api', endpoint, *args))


def releases(repo):
    pages = json.loads(command('gh', 'api', '--paginate', '--slurp', f'repos/{repo}/releases?per_page=100'))
    return [item for page in pages for item in page]


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def read(path):
    return json.loads(path.read_text(encoding='utf-8'))


def sha(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def version_tuple(tag):
    match = re.fullmatch(r'v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)', tag)
    if not match:
        raise ValueError('Release tag must be vMAJOR.MINOR.PATCH, without prerelease suffixes')
    return tuple(map(int, match.groups()))


def context():
    tag = os.environ['RELEASE_TAG']
    version_tuple(tag)
    return tag, tag[1:], os.environ['GITHUB_REPOSITORY']


def output(key, value):
    with Path(os.environ['GITHUB_OUTPUT']).open('a', encoding='utf-8') as target:
        target.write(f'{key}={value}\n')


def validate():
    tag, version, _ = context()
    actual = ET.parse(ROOT / 'src/ToolsTouch.Desktop/ToolsTouch.Desktop.csproj').findtext('.//Version')
    if actual != version:
        raise ValueError('Tag and application version differ')
    if command('git', 'rev-parse', f'refs/tags/{tag}^{{commit}}') != command('git', 'rev-parse', 'HEAD'):
        raise ValueError('Checkout must exactly match the existing version tag')
    if not (ROOT / f'docs/releases/{tag}.md').is_file():
        raise ValueError('Release notes are missing')
    output('tag', tag)
    output('version', version)


def previous():
    tag, _, repo = context()
    # Read all release pages; select the highest earlier published version.
    candidates = []
    for release in releases(repo):
        if release['draft']:
            continue
        try:
            version = version_tuple(release['tag_name'])
        except ValueError:
            continue
        installers = [a for a in release['assets'] if re.fullmatch(r'ToolsTouch-Setup-\d+\.\d+\.\d+-win-x64\.exe', a['name'])]
        if version < version_tuple(tag) and len(installers) == 1:
            candidates.append((version, release['tag_name'], installers[0]))
    folder = ARTIFACTS / 'previous'
    folder.mkdir()
    if not candidates:
        write(folder / 'metadata.json', {'available': False})
        output('available', 'false')
        return
    _, previous_tag, asset = max(candidates, key=lambda candidate: candidate[0])
    destination = folder / 'previous-installer.exe'
    subprocess.run(['gh', 'release', 'download', previous_tag, '--repo', repo,
                    '--pattern', asset['name'], '--output', str(destination)], cwd=ROOT, check=True)
    digest = sha(destination)
    if destination.stat().st_size != asset['size'] or (asset.get('digest') and asset['digest'] != f'sha256:{digest}'):
        raise ValueError('Previous installer asset integrity check failed')
    write(folder / 'metadata.json', {'available': True, 'tag': previous_tag, 'sha256': digest})
    output('available', 'true')


def collect():
    tag, version, repo = context()
    installer = RELEASE / f'ToolsTouch-Setup-{version}-win-x64.exe'
    previous_release = read(ARTIFACTS / 'previous/metadata.json')
    checks = [('installer-check', 'INSTALLER-CHECK.json')]
    if previous_release['available']:
        checks.append(('installer-upgrade', 'INSTALLER-UPGRADE.json'))
    native_count = 0
    for directory, name in checks:
        result = read(ARTIFACTS / directory / 'results.json')
        desktop = read(ARTIFACTS / directory / 'desktop/results.json')
        if not result['passed'] or result['installerSha256'] != sha(installer):
            raise ValueError('Installer verification does not match release artifact')
        if directory == 'installer-upgrade' and result['previousInstallerSha256'] != previous_release['sha256']:
            raise ValueError('Upgrade did not use the selected previous installer')
        if desktop['failures'] or not desktop['results'] or not all(r['passed'] for r in desktop['results']):
            raise ValueError('Native desktop verification failed')
        if desktop['realAccountsUsed'] or desktop['realMailSent']:
            raise ValueError('Release tests must be isolated from real accounts')
        native_count = len(desktop['results'])
        shutil.copy2(ARTIFACTS / directory / 'results.json', RELEASE / name)
    native = ARTIFACTS / checks[-1][0] / 'desktop'
    for source, target in [('results.json', 'WINDOWS-CHECK.json'), ('dpi-results.json', 'DPI-CHECK.json'),
                           ('layout-results.json', 'LAYOUT-CHECK.json')]:
        shutil.copy2(native / source, RELEASE / target)
    shutil.copy2(ARTIFACTS / f'ToolsTouch-win-x64-{tag}.zip', RELEASE)
    shutil.copy2(ARTIFACTS / f'ToolsTouch-win-x64-{tag}/BUILD-MANIFEST.json', RELEASE)
    shutil.copy2(ROOT / f'docs/releases/{tag}.md', RELEASE / 'RELEASE-NOTES.md')
    with zipfile.ZipFile(RELEASE / 'UI-SCREENSHOTS.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(native.glob('*.png')):
            archive.write(path, f'after/{path.name}')
        for path in sorted((ROOT / 'docs/design/images').glob('before-*.png')):
            archive.write(path, f'before/{path.name}')
    write(RELEASE / 'RELEASE-BUILD.json', {
        'version': version, 'sourceCommit': command('git', 'rev-parse', 'HEAD'),
        'workflow': f"https://github.com/{repo}/actions/runs/{os.environ['GITHUB_RUN_ID']}",
        'nativeTestGroups': native_count, 'previousRelease': previous_release,
        'freshInstallVerified': True, 'repairVerified': True, 'uninstallPreservesSyntheticUserFiles': True,
        'signed': False, 'realAccountsUsed': False, 'realMailSent': False,
        'realMonitorTransitionsVerified': False,
    })
    assets = sorted(p for p in RELEASE.iterdir() if p.is_file() and p.name != 'SHA256SUMS.txt')
    (RELEASE / 'SHA256SUMS.txt').write_text(''.join(f'{sha(p)}  {p.name}\n' for p in assets), encoding='utf-8')


def publish():
    tag, version, repo = context()
    source = read(RELEASE / 'RELEASE-BUILD.json')
    if source['sourceCommit'] != command('git', 'rev-parse', f'refs/tags/{tag}^{{commit}}') or source['version'] != version:
        raise ValueError('Release artifact source differs from immutable version tag')
    recovered_run = os.environ.get('RELEASE_BUILD_RUN_ID')
    if recovered_run and source['workflow'] != f'https://github.com/{repo}/actions/runs/{recovered_run}':
        raise ValueError('Downloaded artifact does not belong to the verified build run')
    sums = dict(line.split('  ', 1)[::-1] for line in (RELEASE / 'SHA256SUMS.txt').read_text(encoding='utf-8').splitlines())
    files = sorted(p for p in RELEASE.iterdir() if p.is_file())
    if set(sums) != {p.name for p in files if p.name != 'SHA256SUMS.txt'}:
        raise ValueError('Release file inventory differs from checksum manifest')
    for path in files:
        if path.name in sums and sha(path) != sums[path.name]:
            raise ValueError('Release artifact hash mismatch: ' + path.name)
    existing = next((r for r in releases(repo) if r['tag_name'] == tag), None)
    if existing and not existing['draft']:
        raise ValueError('Published releases are immutable; increment the version instead')
    if not existing:
        subprocess.run(['gh', 'release', 'create', tag, '--verify-tag', '--draft', '--repo', repo,
                        '--title', f'Tools Touch {tag} · 研究与申请工作台',
                        '--notes-file', str(RELEASE / 'RELEASE-NOTES.md')], check=True, cwd=ROOT)
    subprocess.run(['gh', 'release', 'upload', tag, '--repo', repo, '--clobber', *map(str, files)], check=True, cwd=ROOT)
    # The tag endpoint only returns published releases. Authenticated listing
    # includes drafts and supplies the numeric ID needed to verify their assets.
    release = next(r for r in releases(repo) if r['tag_name'] == tag)
    assets = {a['name']: a for a in api(f"repos/{repo}/releases/{release['id']}/assets?per_page=100")}
    if set(assets) != {p.name for p in files}:
        raise ValueError('Remote release asset inventory differs; draft retained')
    for path in files:
        asset = assets[path.name]
        if asset['size'] != path.stat().st_size or asset.get('digest') != f'sha256:{sha(path)}':
            raise ValueError('Remote asset SHA-256 verification failed; draft retained: ' + path.name)
    api(f"repos/{repo}/releases/{release['id']}", '--method', 'PATCH',
        '-F', 'draft=false', '-F', 'prerelease=false', '-f', 'make_latest=true')
    result = api(f'repos/{repo}/releases/tags/{tag}')
    if result['draft'] or result['prerelease']:
        raise ValueError('Formal release publication was not confirmed')
    print(result['html_url'])


def recover():
    tag, _, repo = context()
    run_id = os.environ['RELEASE_BUILD_RUN_ID']
    if not re.fullmatch(r'[1-9]\d*', run_id):
        raise ValueError('Build run ID must be numeric')
    run = api(f'repos/{repo}/actions/runs/{run_id}')
    if run['head_sha'] != command('git', 'rev-parse', f'refs/tags/{tag}^{{commit}}'):
        raise ValueError('Build run does not match the requested version tag')
    if run['status'] != 'completed' or run['path'].split('@')[0] != '.github/workflows/release.yml':
        raise ValueError('Recovery requires a completed release workflow')
    jobs = api(f'repos/{repo}/actions/runs/{run_id}/jobs')['jobs']
    if not any(job['name'] == 'build' and job['conclusion'] == 'success' for job in jobs):
        raise ValueError('Recovery requires a successful complete Windows build and verification job')
    print('Verified build run:', run['html_url'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['validate', 'previous', 'collect', 'publish', 'recover'])
    globals()[parser.parse_args().action]()
