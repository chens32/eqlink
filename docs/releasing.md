# Release procedure

## Public assets

Each GitHub release has exactly **three manually uploaded files**:

| Platform | Name |
| --- | --- |
| Windows x64 one-click installer | `eqlink-client-X.Y.Z.exe` |
| Linux ARM64 server | `eqlink-server-arm64-X.Y.Z.tar.gz` |
| Linux AMD64 server | `eqlink-server-amd64-X.Y.Z.tar.gz` |

The Windows file is the existing Burn bootstrapper, including its official OpenVPN dependency handling; its public filename changes, not its installer behavior. MSI, offline images, tools, validation reports, checksums and build manifests remain local build/evidence files. Documentation and license texts live in the repository and inside the relevant packages. GitHub also generates its own source-code ZIP/tar links from the tag; these are not uploaded assets.

The release body uses English to describe improvements, links the Chinese release Markdown, and links the [Kubernetes installation guide](kubernetes-installation.md) when relevant. Do not attach separate README or release-note files. Preserve exact source/build provenance and third-party license obligations when distributing binaries.

## Assemble and review

After committing source and completing the version-specific checks, build the Windows installer and both Linux architectures. Then assemble local evidence and the upload directory:

```powershell
pwsh -File packaging/build-windows.ps1
pwsh -File packaging/build-server.ps1 -Arch arm64
pwsh -File packaging/build-server.ps1 -Arch amd64
python packaging/package-release.py --acceptance artifacts/VERIFIED-ACCEPTANCE.json
python tools/publish-release.py --version X.Y.Z
```

The acceptance JSON must identify the intended version and retain its actual validation results. The packager creates `artifacts/release/X.Y.Z/upload/` containing exactly the three public binaries. Source, notices, manifests and checksums are retained in its parent directory. `--include-k3s` and `--include-tools` optionally collect internal offline/test outputs; they never add release attachments.

Create the release as a draft if it does not exist, with a verified tag/target and a UTF-8 English notes file. After reviewing the synchronization plan:

```powershell
python tools/publish-release.py --version X.Y.Z --apply --report artifacts/release/X.Y.Z/publish-report.json
```

The tool preserves an existing binary with a matching hash, renames a matching legacy `EqLink-Setup-X.Y.Z.exe` asset, or uploads a missing file. It verifies all three before deleting other manually uploaded assets. A different binary already published under an expected filename is rejected; release a new version instead. The tool does not publish/unpublish releases, move tags, or rewrite notes.

Verify CI/CodeQL, the merge/tag/source relationship, release state and remote SHA-256 digests before publishing a new draft. Keep internal evidence outside Git and retain rollback material. Do not upload `artifacts/release/X.Y.Z/*` with a wildcard; synchronize the `upload/` directory through the tool.

## Kubernetes

Build the container image from the corresponding Linux server archive using `packaging/kubernetes/Dockerfile`, then import it into the selected node. The [English](kubernetes-installation.md) and [Chinese](kubernetes-installation.zh-CN.md) guides cover deployment, enrollment and existing-installation precautions. A separate Kubernetes offline package is not a standard GitHub release attachment.
