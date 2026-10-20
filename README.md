# EqLink

[English](README.md) | [简体中文](README.zh-CN.md)

A private IPv4 network for Windows devices, with a Linux server and two outer transports.

EqLink gives each enrolled machine its own virtual IP. Applications communicate with the server and other devices using ordinary ICMP, TCP and UDP. OpenVPN TCP runs inside **Hysteria 2 / UDP**, with **VLESS + XHTTP + REALITY / TCP** as the fallback.

**[v1.0.0 is available](https://github.com/chens32/eqlink/releases/tag/v1.0.0).** Builds, CI and historical real-network tests cover different scopes. Full acceptance remains incomplete; see [release notes](docs/release-1.0.0.md) and [validation status](docs/validation-status.md).

## Features

- One reusable, passwordless `.eqlink` v2 enrollment bundle for multiple machines.
- Locally generated device keys, separate certificates and outer credentials, per-device revocation.
- Server-assigned persistent IPv4 addresses, or server-validated static address requests.
- Automatic, UDP-only and TCP-only modes; one OpenVPN session at a time.
- Specified physical egress and dedicated DNS for coexistence with other TUN applications, including Meta.
- Windows Filtering Platform protection for the configured virtual IPv4 subnet.
- Windows GUI with connection state, local established TCP count, virtual-interface upload/download rates, diagnostics and per-network domain overrides.
- Debian/Ubuntu amd64/arm64 server packages, with Kubernetes/k3s image-building, deployment and upgrade tools.

## How it works

```text
Application -> EqLink virtual adapter -> OpenVPN TCP -> local selector
                                                    |-- HY2 / UDP ------------|
                                                    |-- XHTTP / REALITY / TCP |-> Linux OpenVPN -> peers
Enrollment and configuration -> the same outer paths -> loopback TLS control API
Authenticated inner health -> virtual adapter -> server virtual IP
```

Importing a bundle prepares a local identity. The first **Connect** enrolls it and obtains a server-confirmed address. Devices then reach each other by virtual IP through the server. Switching outer paths rebuilds OpenVPN; existing application sessions may break. A healthy TCP session is retained when UDP returns.

Only configured outer TCP/UDP ports are public. OpenVPN and administrative endpoints are not public proxies. The outer allowlists end with an explicit deny.

## Get started

### Windows client

1. Use Windows 10/11 x64. Download [eqlink-client-1.0.0.exe](https://github.com/chens32/eqlink/releases/download/v1.0.0/eqlink-client-1.0.0.exe) and compare its SHA-256 with the GitHub release asset digest.
2. Run the installer with administrator approval. The bundle installs EqLink and handles the pinned official OpenVPN prerequisite; uninstalling EqLink preserves OpenVPN and unrelated configurations.
3. Obtain the current v2 `.eqlink` bundle privately from your administrator. Import it under **设备与诊断**, keep automatic addressing or request a static address, then select **连接**.
4. If another TUN controls routing/DNS, select a physical egress interface and dedicated DNS under connection settings. Keep the other TUN running.
5. Contact peers using the assigned virtual IPs. Optional mappings such as `10.88.0.20 service.example.internal` can be saved under **域名覆写**.

The GUI currently uses Simplified Chinese. The installer contains the .NET runtime. The current installer is unsigned. A universal bundle is a reusable enrollment credential: anyone holding an active bundle can register a new identity. Do not upload it to issues or Git.

### Linux server

Use Debian/Ubuntu amd64 or arm64 with TUN, OpenVPN 2.6+, OpenSSL, iproute2 and systemd. Installation and network configuration need root/sudo. Use [server installation](docs/server-installation.md) for host deployment. For Kubernetes/k3s, build an image from the same server archive and follow the [installation guide](docs/kubernetes-installation.md).

Choose a non-overlapping private IPv4 `/24`, a public hostname and HY2 TLS certificate. High-port TCP/UDP NAT is supported. Existing deployments must preserve their CA, REALITY keys, persistent identities and external gateway extensions through a reviewed upgrade/rollback plan.

## Traffic and protection boundaries

- The current network is IPv4-only, single-server and device-to-device. It does not provide arbitrary LAN routing, overlapping-site routing, global Internet egress, peer discovery or server HA.
- WFP blocks the configured virtual subnet outside the confirmed EqLink adapter. Administrators can change protection. Explicit application proxies that receive a hostname/request before an IP connection are outside this subnet-filtering boundary.
- Domain overrides maintain an EqLink-owned block in the Windows hosts file. Other entries are preserved; conflicts are rejected. Applications using their own DNS/DoH or a remote proxy may bypass system resolution. Overrides remain applied while the service runs, including while the VPN is disconnected.
- TCP count is the local machine's established overlay TCP connections, not the server's online-device count. Rates measure virtual-interface bytes, not encrypted outer overhead.
- Coexistence tests and saved captures do not guarantee immunity to all third-party routing changes or resistance to detection/blocking.

## Build and test

Use the SDK pinned by `global.json`, Python 3.12+ and PowerShell. Windows packaging uses checksum-pinned WiX and dependencies from `packaging/third_party.lock.json`.

```powershell
python tools/check-repository.py
python tools/check-release.py
dotnet run --project src/EqLink.Tests/EqLink.Tests.csproj -c Release
python packaging/k3s/test-upgrade.py
python packaging/k3s/test-migration.py
python packaging/k3s/test_bundle_trust.py
pwsh -File packaging/build-windows.ps1
pwsh -File packaging/build-server.ps1 -Arch amd64
pwsh -File packaging/build-server.ps1 -Arch arm64
```

ARM64 image validation additionally uses Debian WSL, an isolated rootfs and QEMU. See [development](docs/development.md), [acceptance tools](packaging/acceptance/README.zh-CN.md) and the release packaging scripts. Build outputs, state, credentials and private test evidence remain outside Git.

CI runs source/history secret checks, offline deployment checks, Linux/Windows core tests, Windows compilation and CodeQL. It does not deploy to your server, modify WFP, install/uninstall EqLink or establish real-network acceptance.

## Project information

- [Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md) · [Changelog](CHANGELOG.md)
- [v2 protocol](docs/protocol-v2.md) · [Domain overrides](docs/hosts-overrides.md) · [Physical egress](docs/windows-physical-egress.md)
- [Release notes](docs/release-1.0.0.md) · [Release procedure](docs/releasing.md) · [Third-party components](packaging/THIRD_PARTY.md) · [License review](docs/licensing.md)

## License

EqLink's original source is **GPL-3.0-only**; see [LICENSE](LICENSE). OpenVPN, Hysteria, Xray, .NET, WiX and image packages retain their own licenses and corresponding-source requirements. See [license review](docs/licensing.md).
