#!/usr/bin/env python3
"""Check source publication boundaries without printing any matching secret."""
import argparse
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--tracked-only', action='store_true', help='Check exactly the Git index; otherwise include non-ignored untracked files.')
args = parser.parse_args()
command = ['git', 'ls-files', '--cached', '-z']
if not args.tracked_only:
    command += ['--others', '--exclude-standard']
paths = sorted({p.decode('utf-8') for p in subprocess.check_output(command, cwd=ROOT).split(b'\0') if p})
forbidden_parts = {'artifacts', 'bin', 'obj', '.dotnet', '.nuget', '.vs', '.venv', '__pycache__', '.kube', 'pki', 'secrets', '.codex', '.agents'}
forbidden_suffixes = {'.eqlink', '.dpapi', '.ovpn', '.key', '.pem', '.pfx', '.p12', '.cer', '.crt', '.jks', '.keystore', '.exe', '.dll', '.msi', '.cab', '.zip', '.tar', '.gz', '.tgz', '.7z', '.pcap', '.pcapng', '.etl', '.log', '.dmp', '.bak'}
forbidden_names = {'server-settings.json', 'state.json', 'pending-state.json', 'identity.json', 'address.json', 'profile.json', 'egress.json', 'protection.json', 'mode.json', 'hosts.json', 'hosts-overrides.json', 'registration.json', 'grant.json', 'grants.json', 'kubeconfig', 'id_rsa', 'id_ed25519'}
private_key = re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----\s*(?:\\[rn]|\r?\n)?[A-Za-z0-9+/=]{32,}')
credential_url = re.compile(rb'https?://[^\s/"<>:@]{1,80}:[^\s/"<>@]{4,}@')
findings = []
checked = 0
for name in paths:
    relative = PurePosixPath(name)
    lowered = name.lower()
    base = relative.name.lower()
    bad_name = (base in forbidden_names or base.startswith('.xray-user-') or 'credentials' in base or '.passphrase' in base or '.password' in base or base.endswith('.pass')
                or (base.startswith('.env') and base != '.env.example') or base.startswith('kubeconfig.'))
    bad_path = any(part.lower() in forbidden_parts for part in relative.parts)
    if lowered.startswith('packaging/k3s/private/') or bad_name or bad_path or relative.suffix.lower() in forbidden_suffixes:
        findings.append((name, 'private-or-generated-path'))
    path = ROOT / name
    if path.is_symlink():
        findings.append((name, 'symlink-requires-review'))
        continue
    if not path.is_file():
        findings.append((name, 'missing-index-file'))
        continue
    if path.stat().st_size > 1_048_576:
        findings.append((name, 'source-file-exceeds-1-MiB'))
        continue
    data = path.read_bytes()
    checked += 1
    for rule, pattern in (('private-key-material', private_key), ('credential-in-url', credential_url)):
        match = pattern.search(data)
        if match:
            findings.append((f'{name}:{data[:match.start()].count(bytes([10])) + 1}', rule))
if findings:
    for name, rule in findings:
        print(f'{name}: {rule}', file=sys.stderr)
    raise SystemExit(1)
print(f'Repository publication checks passed: {checked} source files; no forbidden paths or matching key material.')
print('This boundary check complements Gitleaks; it does not prove absence of all secrets.')
