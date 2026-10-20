# EqLink v1.0.0 draft release

This is the first proposed numbered release of the Windows client and Linux server together. **The GitHub release remains a draft.** A version number and green CI do not establish complete Windows lifecycle, real-network or security acceptance.

## Included

- Windows x64 WPF client, background service, independent domain overrides, local TCP connection count and virtual-interface rates.
- OpenVPN TCP over HY2/UDP, with VLESS + XHTTP + REALITY/TCP fallback and a single inner session.
- Reusable v2 enrollment bundle, locally generated device keys, per-device authentication and server-confirmed automatic/static IPv4 addresses.
- Specified physical egress, dedicated DNS and WFP protection for the configured virtual IPv4 network.
- Debian/Ubuntu amd64/arm64 server packages, ARM64 k3s offline build/deployment tools, inspection and bounded diagnostics.
- English default README, Simplified Chinese README, GPLv3 license and public repository security/CI policies.

## Review fixes

- Limit control API responses while reading, including unknown-length/chunked bodies; reject oversized declared responses before reading them.
- Reject undefined numeric address modes instead of treating them as automatic allocation.
- Centralize the product version, rotate the MSI ProductCode for the new release, and retain UpgradeCodes.
- Preserve license notices in self-contained binaries and identify unresolved corresponding-source distribution requirements.
- Extend ignored runtime/credential/build paths, restore PR/main CI, and add pinned CodeQL analysis and dependency updates.

## Compatibility

The enrollment protocol remains v2. Existing v2 identities and network trust remain compatible. v1 password-encrypted per-device bundles remain retired. An existing server deployment needs a backup and a reviewed upgrade plan; replacing binaries does not authorize a live deployment. External management gateways are independently maintained and must support pending devices without an assigned IP.

Windows upgrades must preserve `%ProgramData%\EqLink` identity/settings and unrelated OpenVPN files. A full new v1.0.0 installer lifecycle run is a release gate; prior 0.3.2/0.3.3 upgrade evidence is historical evidence.

## Acceptance boundaries

Fresh v1.0.0 builds/tests and the exact commit are recorded in the release's `ACCEPTANCE.json` and `release-manifest.json`. Historical real-network results are summarized in [validation status](validation-status.md). They are not relabeled as v1.0.0 tests.

Remaining gates include two current-version Windows clients, repeated peer-traffic failover, static address/revocation scenarios, full Windows install/uninstall/reboot/rollback, WFP/BFE and NIC/DNS change matrices, long-duration stability, native cluster restore/rollback, Authenticode signing, and complete third-party corresponding-source compliance for redistributed installers/images. The release record must keep `fullOriginalAcceptanceComplete=false` while these remain open.

## Artifacts

- `EqLink-Setup-1.0.0.exe`: one-entry Windows installer with official OpenVPN dependency handling.
- `EqLink-1.0.0.msi`: standalone EqLink MSI; an appropriate OpenVPN installation is required.
- `eqlink-server-amd64-1.0.0.tar.gz` and `eqlink-server-arm64-1.0.0.tar.gz`: self-contained EqLink server plus pinned outer transports; host OpenVPN/OpenSSL remain prerequisites.
- `eqlink-k3s-arm64-1.0.0.tar.gz`: offline ARM64 image, deployment/upgrade tools and isolated validation record.
- Source archive, license/notices, checksums, provenance and acceptance records.

EqLink installers are unsigned unless explicitly identified as signed in the manifest. Verify hashes against the trusted release source; a checksum is not an Authenticode signature.
