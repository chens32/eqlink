# v1.0.0 source and publication review

## Scope

Review the local publication tree, including changes since the last upstream commit and newly added enrollment, control API, address, health, egress, hosts, statistics, GUI and packaging code. Focus on credentials/publication boundaries, TLS and authenticated endpoints, process ownership, state transactions, WFP/route restrictions, deployment preservation and release provenance. This is a maintainer review, not an independent security audit or full runtime acceptance.

## Findings addressed

| Finding | Change | Verification |
| --- | --- | --- |
| Control response size checked after buffering | Enforce 128,000-byte limit while reading; precheck declared size; cap unknown-length reads | Exact-boundary, oversized declared body and unknown-length body tests, including a check that only 128,001 bytes are consumed |
| Undefined numeric address mode accepted | Require a defined AddressMode before choosing an address | Invalid enum request rejected with ADDRESS_MODE_INVALID |
| Different versions embedded in client, server, installers and tools | Central product version 1.0.0, MSI product identity updated, tools report assembly version | Metadata check plus published binary inspection |
| Missing runtime notices and project license | GPLv3 selected by owner; extract verified .NET notices and retain WiX/outer notices | Notice staging and dependency hashes; complete third-party source compliance remains a publication gate |
| Incomplete ignored runtime/build paths | Add address/registration/hosts/mode, private directories, database and installer intermediate paths; strengthen boundary checker | Source tree boundary check and redacted history/source Gitleaks scans |
| Public repository had no active protection/CI | PR/main CI, CodeQL, dependency updates, ownership/templates and repository protection configuration | GitHub settings readback and PR checks are recorded separately from source configuration |

## Boundaries reviewed and retained

- Per-machine local keys and DPAPI state; universal bundles contain reusable enrollment credentials and must remain private.
- TLS custom trust with hostname checks; issued device authentication also checks active durable state and exact issued certificate. OpenVPN CRLs remain separate from application authentication.
- Enrollment and address writes use the state lock and commit journal; read-only snapshots do not establish crash-recovery acceptance.
- OpenVPN is loopback-only and outer routing ends with deny; local selector uses an internal token and fixed destinations.
- IPC requires an administrator for imports/configuration writes; public diagnostics are allowlisted.
- WFP permits only the confirmed virtual adapter; structural failures and authenticated reachability have separate handling.
- Hosts changes preserve unrelated bytes, reject conflicts, serialize revisions and bound sharing-lock retries.
- k3s uses a fixed node, one replica and PVC; upgrades preserve external startup/probe/mount/gateway additions and require reviewed backup/rollback conditions.

## Outstanding gates

The [v1.0.0 draft notes](release-1.0.0.md) list the current runtime/installer gates. OpenVPN/MSI components, WiX bootstrapper and Debian image packages have separate source obligations; source links and EqLink's source alone are not claimed to close those obligations. Installer signing is also open. Public API enrollment rate/capacity testing and hostile local IPC resource-exhaustion testing remain future hardening work.

Do not commit machine-local evidence or relabel historical 0.x network tests as 1.0.0 acceptance. The actual draft manifest must identify a clean committed tree and the uploaded binary hashes.
