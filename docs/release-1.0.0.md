# EqLink v1.0.0

## What's new

- Added reusable v2 enrollment bundles with passwordless import, automatic device registration, and independent credentials for each machine.
- Added persistent server-assigned IPv4 addresses and server-validated static address requests.
- Improved the Windows interface with connection status, active TCP connection counts, upload/download rates, and diagnostic export.
- Added per-network domain overrides for accessing VPN services by hostname.
- Improved Hysteria 2 cold starts and UDP recovery, with VLESS + XHTTP + REALITY fallback when UDP is unavailable.
- Added physical-interface selection, dedicated DNS, and WFP protection for the virtual subnet to improve coexistence with other TUN applications.
- Hardened control API response limits and address-mode validation.

## Downloads

The three release assets are `eqlink-client-1.0.0.exe`, `eqlink-server-arm64-1.0.0.tar.gz` and `eqlink-server-amd64-1.0.0.tar.gz`. The client is the one-click Windows x64 installer. Match the server archive to your Linux machine or Kubernetes node architecture.

## Kubernetes / k3s

Build an image from the corresponding Linux server archive, import it into the selected node, and use the repository's deployment tool. The guide covers TUN prerequisites, persistent storage, high-port NodePorts, enrollment and existing-installation upgrades: [Kubernetes installation](kubernetes-installation.md).

[Chinese release notes](release-1.0.0.zh-CN.md)
