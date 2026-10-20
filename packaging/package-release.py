#!/usr/bin/env python3
"""Collect v1.0.0 draft artifacts from a clean committed source tree."""
import argparse
import gzip
import hashlib
import io
import json
import pathlib
import shutil
import subprocess
import tarfile
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

ROOT = pathlib.Path(__file__).resolve().parent.parent
VERSION = ET.parse(ROOT / 'Directory.Build.props').findtext('./PropertyGroup/Version')
OUT = ROOT / 'artifacts/release' / VERSION


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--acceptance', required=True, type=pathlib.Path)
    parser.add_argument('--without-k3s', action='store_true')
    parser.add_argument('--without-tools', action='store_true')
    args = parser.parse_args()
    status = git('status', '--porcelain', '--untracked-files=all')
    if status.strip():
        raise SystemExit('Commit source changes before packaging; artifacts must identify an exact clean Git tree')
    acceptance = json.loads(args.acceptance.read_text(encoding='utf-8-sig'))
    if acceptance.get('version') != VERSION or acceptance.get('fullOriginalAcceptanceComplete') is not False:
        raise SystemExit('Draft acceptance record must match version and preserve incomplete gates')
    OUT.mkdir(parents=True, exist_ok=True)
    files = {
        f'EqLink-Setup-{VERSION}.exe': 'artifacts/package/EqLink-Setup.exe',
        f'EqLink-{VERSION}.msi': 'artifacts/package/EqLink.msi',
        f'eqlink-server-amd64-{VERSION}.tar.gz': 'artifacts/server/eqlink-server-amd64.tar.gz',
        f'eqlink-server-arm64-{VERSION}.tar.gz': 'artifacts/server/eqlink-server-arm64.tar.gz',
        'README.md': 'README.md', 'README.zh-CN.md': 'README.zh-CN.md',
        'RELEASE-NOTES.md': f'docs/release-{VERSION}.md',
        'RELEASE-NOTES.zh-CN.md': f'docs/release-{VERSION}.zh-CN.md',
        'LICENSE': 'LICENSE', 'NOTICE': 'NOTICE', 'THIRD_PARTY.md': 'packaging/THIRD_PARTY.md',
        'LICENSING.md': 'docs/licensing.md',
    }
    if not args.without_k3s:
        files[f'eqlink-k3s-arm64-{VERSION}.tar.gz'] = f'artifacts/k3s/eqlink-k3s-arm64-{VERSION}.tar.gz'
    if not args.without_tools:
        files[f'eqlink-tests-{VERSION}.zip'] = f'artifacts/tools/eqlink-tests-{VERSION}.zip'
    for name, source in files.items():
        if not (ROOT / source).is_file():
            raise SystemExit('Required release input missing: ' + source)
        shutil.copy2(ROOT / source, OUT / name)
    head = git('rev-parse', 'HEAD').decode().strip()
    # git archive otherwise converts text=auto entries using Windows core.eol.
    archive = git('-c', 'core.autocrlf=false', '-c', 'core.eol=lf', 'archive', '--format=tar', f'--prefix=eqlink-{VERSION}/', head)
    with (OUT / f'eqlink-source-{VERSION}.tar.gz').open('wb') as stream:
        with gzip.GzipFile(fileobj=stream, mode='wb', mtime=0) as compressed:
            compressed.write(archive)
    shutil.copy2(args.acceptance, OUT / 'ACCEPTANCE.json')
    provenance = None
    if not args.without_k3s:
        provenance = json.loads((ROOT / 'artifacts/k3s/PROVENANCE.json').read_text())
        for name, checksum in provenance['sourceSha256'].items():
            if digest(ROOT / name) != checksum:
                raise SystemExit('k3s source changed after image build: ' + name)
        shutil.copy2(ROOT / 'artifacts/k3s/VALIDATION.json', OUT / 'K3S-VALIDATION.json')
    tracked = [p.decode() for p in git('ls-files', '-z').split(b'\0') if p]
    source_hashes = {name: hashlib.sha256(git('show', head + ':' + name)).hexdigest() for name in tracked}
    with tarfile.open(fileobj=io.BytesIO(archive)) as source:
        archive_hashes = {member.name.removeprefix(f'eqlink-{VERSION}/'): hashlib.sha256(source.extractfile(member).read()).hexdigest()
                          for member in source.getmembers() if member.isfile()}
    if archive_hashes != source_hashes:
        raise SystemExit('Source archive differs from committed blobs; refusing to publish a mismatched manifest')
    artifacts = {p.name: {'bytes': p.stat().st_size, 'sha256': digest(p)} for p in sorted(OUT.iterdir()) if p.is_file() and p.name not in ('release-manifest.json', 'SHA256SUMS')}
    manifest = {'schemaVersion': 1, 'version': VERSION, 'channel': 'draft', 'createdUtc': datetime.now(timezone.utc).isoformat(),
                'source': {'gitHead': head, 'gitTree': git('rev-parse', 'HEAD^{tree}').decode().strip(), 'workingTreeDirty': False, 'sha256': source_hashes},
                'artifacts': artifacts, 'k3sProvenance': provenance, 'fullOriginalAcceptanceComplete': False,
                'windowsAuthenticodeSigned': False, 'publicServerUpgraded': False,
                'thirdPartyRedistributionReviewComplete': acceptance.get('thirdPartyRedistributionReviewComplete', False)}
    (OUT / 'release-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    (OUT / 'SHA256SUMS').write_text(''.join(digest(p) + '  ' + p.name + '\n' for p in sorted(OUT.iterdir()) if p.is_file() and p.name != 'SHA256SUMS'), encoding='utf-8')
    print('Draft release assembled from', head, 'at', OUT)


if __name__ == '__main__':
    main()
