#!/usr/bin/env python3
"""Collect verified build outputs with a source snapshot; never includes runtime state."""
import hashlib
import json
import pathlib
import shutil
import subprocess
import tarfile
from datetime import datetime, timezone

ROOT=pathlib.Path(__file__).resolve().parent.parent
OUT=ROOT/'artifacts/release/0.2.0'

def digest(path):
    with path.open('rb') as stream: return hashlib.file_digest(stream,'sha256').hexdigest()

def sources():
    roots=[ROOT/'src',ROOT/'packaging',ROOT/'docs']
    for folder in roots:
        for path in sorted(folder.rglob('*')):
            if not path.is_file() or any(p in ('bin','obj','__pycache__','private') for p in path.relative_to(ROOT).parts): continue
            if path.suffix not in ('.cs','.csproj','.py','.sh','.ps1','.wxs','.json','.md'): continue
            yield path
    for name in ('README.md','Directory.Build.props','.gitignore','.gitattributes','global.json'):
        path=ROOT/name
        if path.is_file(): yield path

def main():
    OUT.mkdir(parents=True,exist_ok=True)
    files={
        'EqLink-Setup-0.2.0.exe':'artifacts/package/EqLink-Setup.exe',
        'eqlink-server-amd64-0.2.0.tar.gz':'artifacts/server/eqlink-server-amd64.tar.gz',
        'eqlink-server-arm64-0.2.0.tar.gz':'artifacts/server/eqlink-server-arm64.tar.gz',
        'eqlink-k3s-arm64-test-0.2.0.tar.gz':'artifacts/k3s/eqlink-k3s-arm64-test-0.2.0.tar.gz',
        'eqlink-tests-0.2.0.zip':'artifacts/tools/eqlink-tests-0.2.0.zip'
    }
    for name,source in files.items(): shutil.copy2(ROOT/source,OUT/name)
    source_files={str(p.relative_to(ROOT)).replace('\\','/'):digest(p) for p in sources()}
    archive_path=OUT/'eqlink-source-0.2.0.tar.gz'
    with tarfile.open(archive_path,'w:gz') as archive:
        for name in sorted(source_files):
            path=ROOT/name
            info=archive.gettarinfo(str(path),arcname='eqlink-0.2.0/'+name)
            info.uid=info.gid=0;info.uname=info.gname='';info.mode=0o755 if path.suffix=='.sh' else 0o644
            with path.open('rb') as stream: archive.addfile(info,stream)
    shutil.copy2(ROOT/'docs/release-0.2.0.md',OUT/'README.zh-CN.md')
    shutil.copy2(ROOT/'artifacts/validation-0.2.0/ACCEPTANCE.json',OUT/'ACCEPTANCE.json')
    provenance=json.loads((ROOT/'artifacts/k3s/PROVENANCE.json').read_text())
    k3s_validation=json.loads((ROOT/'artifacts/k3s/VALIDATION.json').read_text())
    # Prevent a final archive from claiming source files newer than its image build.
    for name,checksum in provenance['sourceSha256'].items():
        if digest(ROOT/name)!=checksum: raise SystemExit('k3s source changed after image build: '+name)
    head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    status=subprocess.check_output(['git','status','--porcelain'],cwd=ROOT,text=True)
    artifacts={p.name:{'sha256':digest(p),'bytes':p.stat().st_size} for p in sorted(OUT.iterdir()) if p.is_file() and p.name not in ('release-manifest.json','SHA256SUMS')}
    record={'schemaVersion':1,'version':'0.2.0','channel':'test','createdUtc':datetime.now(timezone.utc).isoformat(),
            'source':{'gitHead':head,'workingTreeDirty':bool(status),'origin':'uncommitted-workspace-snapshot' if status else 'git-checkout','sha256':source_files},
            'artifacts':artifacts,'k3sProvenance':provenance,'k3sValidation':k3s_validation,
            'publicServerUpgraded':False,'fullOriginalAcceptanceComplete':False}
    (OUT/'release-manifest.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    lines=[digest(p)+'  '+p.name for p in sorted(OUT.iterdir()) if p.is_file() and p.name!='SHA256SUMS']
    (OUT/'SHA256SUMS').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(str(OUT))

if __name__=='__main__': main()
