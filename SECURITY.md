# Security policy

## Supported versions

v1.0.0 is the first proposed release and is currently prepared as a **draft**. Only the latest maintained release will receive fixes. Earlier 0.x builds are development/test snapshots. No version has completed an independent security audit or the full runtime/installer acceptance matrix; see [validation status](docs/validation-status.md).

## Private vulnerability reports

Use [GitHub private vulnerability reporting](https://github.com/chens32/eqlink/security/advisories/new) for exploitable vulnerabilities, credential exposure or traffic-boundary failures. If the form is unavailable, arrange a private contact channel with the maintainer before sharing details. Public issues should contain sanitized symptoms, versions and reproduction steps only. Response times are best effort; no support SLA is promised.

Never post enrollment bundles, bearer tokens, UUID credentials, passwords, private keys, CA private material, Cookies, raw decrypted runtime configuration or unsanitized packet captures. Diagnostics may still identify networks, routes and hosts; review them before sharing.

## Repository controls

- Runtime state, bundles, certificates/keys, deployment credentials, archives, captures and local evidence are excluded from source control.
- CI uses source-boundary checks and redacted Gitleaks history scanning. CodeQL and dependency updates complement review; they do not prove absence of vulnerabilities.
- Pull requests and checks are required before merging to main. Actions use pinned commits and least-privilege tokens; untrusted PR code must never run on privileged/self-hosted production hosts or receive deployment credentials.
- Real server changes require target-specific authorization, private backups and an independent recovery path. Release artifacts identify source commits and SHA-256; draft installers are unsigned unless stated otherwise.

If a secret enters Git, revoke/rotate it first and coordinate history/copy cleanup. Deleting it in a later commit does not retract the original exposure. Do not upload private incident evidence merely to demonstrate remediation.

## Security model boundaries

A valid universal enrollment bundle allows its holder to create new device identities. Revoking a device does not revoke the bundle; revoke the enrollment grant separately when appropriate. All enrolled peers currently share the virtual network.

WFP protection applies to the configured IPv4 virtual subnet. Explicit application proxies, administrators changing local policy, and traffic outside that subnet are outside this boundary. Transport reachability and captures without observed leaks do not establish resistance to traffic identification or blocking.
