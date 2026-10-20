#!/usr/bin/env python3
"""Check committed release metadata and documentation before building or merging."""
import pathlib
import hashlib
import re
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
props = ET.parse(ROOT / 'Directory.Build.props').getroot()
version = props.findtext('./PropertyGroup/Version')
assert version and re.fullmatch(r'\d+\.\d+\.\d+', version), 'Invalid product version'
assert props.findtext('./PropertyGroup/AssemblyVersion') == version + '.0'
assert props.findtext('./PropertyGroup/FileVersion') == version + '.0'
assert props.findtext('./PropertyGroup/PackageLicenseExpression') == 'GPL-3.0-only'
ns = {'w': 'http://wixtoolset.org/schemas/v4/wxs'}
for name, element in [('EqLink.wxs', 'Package'), ('EqLink.Bundle.wxs', 'Bundle')]:
    assert ET.parse(ROOT / 'packaging' / name).find('w:' + element, ns).get('Version') == version, name
for name in ('README.md', 'README.zh-CN.md', 'LICENSE', 'SECURITY.md', 'CONTRIBUTING.md', '.github/CODEOWNERS', 'docs/licensing.md', f'docs/release-{version}.md'):
    assert (ROOT / name).is_file(), 'Missing public release file: ' + name
assert 'GNU GENERAL PUBLIC LICENSE' in (ROOT / 'LICENSE').read_text(encoding='utf-8-sig')
assert hashlib.sha256((ROOT / 'packaging/licenses/WiX-5.0.2-LICENSE.txt').read_bytes()).hexdigest() == 'dfdf2048787635215a6baf3b9d461dee89a2904d246787258a44f072a98d4786'
assert 'draft' in (ROOT / f'docs/release-{version}.md').read_text(encoding='utf-8').lower()
for name in ('README.md', 'README.zh-CN.md'):
    content = (ROOT / name).read_text(encoding='utf-8')
    for target in re.findall(r'\]\(([^)]+)\)', content):
        if '://' in target or target.startswith('#'):
            continue
        assert (ROOT / target.split('#')[0]).exists(), f'{name}: broken link {target}'
print(f'Release metadata, GPLv3, bilingual entry points and README links passed: {version}')
