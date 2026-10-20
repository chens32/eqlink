# Linux server installation

## Requirements

- Debian/Ubuntu with systemd, amd64 or arm64, `/dev/net/tun`, OpenVPN 2.6+, OpenSSL, iproute2 and Python 3.
- Root (or sudo) for installation, TUN configuration and service management. Administrative access to state is required for enrollment/revocation.
- A reachable IPv4 address or DNS name, a TLS certificate/key for HY2, and TCP/UDP outer ports. High-port NAT is supported.
- A private IPv4 `/24` that does not overlap the client/server local networks. `.1` belongs to the server, `.1` through `.9` are reserved, devices use `.10` through `.254`.

## New host installation

Download the matching archive from the release and verify `SHA256SUMS`. Extract it into a dedicated directory. Install host prerequisites with the distribution package manager:

```bash
sudo apt-get update
sudo apt-get install openvpn openssl iproute2 python3
```

Copy `server-settings.example.json` to a private file outside source control and replace its placeholders. Generate the REALITY key pair using the bundled `xray x25519`; keep the private key out of terminal logs and public issues. Set correct certificate paths and expose only the configured outer ports.

```bash
chmod 0755 EqLink.Server hysteria xray
sudo bash install-server.sh /root/eqlink-private/server-settings.json
sudo /usr/local/bin/eqlink-server inspect --json
```

The installer creates `eqlink.service`. State lives in `/etc/eqlink`; the supervisor owns OpenVPN and both outer processes. OpenVPN/control/authentication/management listeners remain on loopback; authenticated inner health additionally listens on the virtual server address. Do not forward these internal ports publicly.

Create a reusable enrollment bundle in a private directory:

```bash
sudo install -d -m 0700 /root/eqlink-private
sudo /usr/local/bin/eqlink-server enrollment-create \
  --name network --bundle /root/eqlink-private/network.eqlink
```

For a private HY2 CA, add the public root using the [bundle trust tool](../packaging/k3s/bundle_trust.py); use its `--help` and verify the fingerprint independently. Do not use the CA private key or disable TLS verification. The universal bundle is a bearer enrollment credential; transfer it privately. A holder can enroll a new identity while the grant is active.

## Existing installation

Do not rerun initialization or regenerate network keys. Use `eqlink-server backup --output` with a private output path outside `/etc/eqlink`, preserve bootstrap settings and certificates, and retain old binaries before a coordinated upgrade. v1 legacy state requires the explicit [v2 migration](migration-0.3.0.md). Test rollback through an administrative path that does not depend on EqLink.

## k3s

Use the ARM64 offline package and its [deployment/upgrade instructions](../packaging/k3s/README.zh-CN.md). Import the image on the explicitly selected node; verify the TUN resource is allocatable. The package uses one replica, Recreate, a PVC, fixed-node scheduling, high NodePorts, `NET_ADMIN` only, read-only root, no privilege escalation and RuntimeDefault seccomp. `deploy.py` is for first installation; `upgrade.py` prepares a reviewed plan and preserves external startup/probe/mount/gateway extensions.

The package does not install a TUN device plugin or change the router's NAT mappings. Existing cluster CAs, REALITY keys, identities, addresses, VIPs and external Rancher gateways must be preserved during upgrade.
