#!/usr/bin/env python3
"""Collect notices from hash-verified dependency archives into a build stage."""
import argparse
import json
import pathlib
import shutil
import zipfile

from fetch_dependencies import DEST, LOCK, ROOT, fetch


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target', required=True, type=pathlib.Path)
    parser.add_argument('--runtime-pack', action='append', default=[])
    parser.add_argument('--windows', action='store_true')
    args = parser.parse_args()
    target = args.target.resolve()
    if not target.is_relative_to((ROOT / 'artifacts').resolve()):
        raise SystemExit('License staging must remain inside artifacts')
    notices = target / 'licenses'
    notices.mkdir(parents=True, exist_ok=True)
    for name in ('LICENSE', 'NOTICE'):
        shutil.copy2(ROOT / name, notices / ('EqLink-' + name))
    shutil.copy2(ROOT / 'packaging/THIRD_PARTY.md', notices / 'THIRD_PARTY.md')
    shutil.copy2(ROOT / 'docs/licensing.md', notices / 'REDISTRIBUTION.md')
    keys = list(args.runtime_pack)
    if args.windows:
        shutil.copy2(ROOT / 'packaging/licenses/WiX-5.0.2-LICENSE.txt', notices / 'WiX-5.0.2-LICENSE.txt')
    inventory = []
    for key in dict.fromkeys(keys):
        fetch(key)
        archive = DEST / pathlib.Path(LOCK[key]['url']).name
        extracted = []
        with zipfile.ZipFile(archive) as package:
            for member in package.namelist():
                name = pathlib.PurePosixPath(member).name
                if not name or not any(word in name.lower() for word in ('license', 'thirdpartynotices', 'third-party-notices', 'third_party_notices')):
                    continue
                if pathlib.PurePosixPath(member).suffix.lower() not in ('.txt', '.md', '.rtf', ''):
                    continue
                info = package.getinfo(member)
                if info.file_size > 2_000_000:
                    raise SystemExit('Unexpected notice size: ' + key)
                output = key + '-' + member.replace('/', '_')
                (notices / output).write_bytes(package.read(member))
                extracted.append(output)
        if not extracted:
            raise SystemExit('No license notices found in verified dependency: ' + key)
        inventory.append({'component': key, 'version': LOCK[key]['version'], 'url': LOCK[key]['url'], 'sha256': LOCK[key]['sha256'], 'notices': extracted})
    (notices / 'runtime-notices.json').write_text(json.dumps(inventory, indent=2) + '\n', encoding='utf-8')
    print('Collected project and verified runtime notices:', len(inventory), 'archives')


if __name__ == '__main__':
    main()
