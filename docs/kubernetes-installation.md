# Kubernetes / k3s installation

[English](kubernetes-installation.md) | [简体中文](kubernetes-installation.zh-CN.md)

Use the **same Linux server archive** as a host installation. Build a container image locally; there is no separate Kubernetes image archive among the three standard release attachments. This procedure creates a **new** deployment. Existing installations use the upgrade section below.

## Requirements

- One selected Ready Linux node, `amd64` or `arm64`, with `/dev/net/tun` and an installed TUN device plugin. The examples use the existing resource name `armhpc.home.arpa/tun`; substitute your plugin's resource.
- A working StorageClass (`local-path` in k3s), a persistent volume, and administrative Kubernetes permissions. Importing an image into a node runtime and maintaining TUN require root/sudo on that node.
- A Linux build machine with Git, Python 3, curl, jq and Docker Buildx. Build natively for the selected architecture, or explicitly configure cross-architecture build emulation yourself.
- A public hostname, its HY2 TLS certificate/private key, non-overlapping private IPv4 `/24`, and TCP/UDP NodePorts. Defaults are TCP `30443`, UDP `30444`.
- Connectivity from the build machine to GitHub and Debian repositories. No .NET SDK is needed to wrap the prebuilt server archive.

The Pod runs one replica with `Recreate`, fixed-node scheduling, PVC-backed `/etc/eqlink`, `privileged: false`, `NET_ADMIN` only, read-only root filesystem, no privilege escalation and `RuntimeDefault` seccomp. The TUN plugin supplies the device; the deployer rejects missing or occupied resources. Namespace Pod Security policy must permit TUN/NET_ADMIN; the generated namespace uses the `privileged` admission profile, while the container itself remains non-privileged.

## 1. Download and build

Run on the Linux build machine. Use `ARCH=amd64` for an AMD64 node. Clone the current deployment tooling, and record its commit separately from the binary release tag.

```bash
set -euo pipefail
VERSION=1.0.0
ARCH=arm64
IMAGE="docker.io/eqlink/server:${VERSION}-${ARCH}"
WORK="$HOME/eqlink-kubernetes-${VERSION}-${ARCH}"
mkdir -p "$WORK"
git clone https://github.com/chens32/eqlink.git "$WORK/source"
cd "$WORK/source"
git rev-parse HEAD > "$WORK/deployment-tools-commit.txt"

CONTEXT="$WORK/image-context"
mkdir -p "$CONTEXT"
ARCHIVE="eqlink-server-${ARCH}-${VERSION}.tar.gz"
curl --fail --location --silent --show-error \
  "https://api.github.com/repos/chens32/eqlink/releases/tags/v${VERSION}" \
  --output "$WORK/release.json"
EXPECTED=$(jq -er --arg name "$ARCHIVE" \
  '.assets[] | select(.name == $name and .state == "uploaded") | .digest | select(startswith("sha256:")) | ltrimstr("sha256:")' \
  "$WORK/release.json")
curl --fail --location --show-error \
  "https://github.com/chens32/eqlink/releases/download/v${VERSION}/${ARCHIVE}" \
  --output "$CONTEXT/$ARCHIVE"
printf '%s  %s\n' "$EXPECTED" "$CONTEXT/$ARCHIVE" | sha256sum --check -
cp packaging/k3s/device-request.py "$CONTEXT/"
docker buildx build --load --platform "linux/$ARCH" \
  --build-arg "SERVER_ARCHIVE=$ARCHIVE" \
  --tag "$IMAGE" --file packaging/kubernetes/Dockerfile "$CONTEXT"
docker image inspect --format '{{.Id}} {{.Architecture}}' "$IMAGE" \
  > "$WORK/image-identity.txt"
docker save --output "$WORK/eqlink-server-${ARCH}-image.tar" "$IMAGE"
sha256sum "$WORK/eqlink-server-${ARCH}-image.tar"
```

The Dockerfile verifies all three ELF executables match the requested architecture. It retains the package's notices in `/opt/eqlink` and distribution copyright files. Keep credentials, `.eqlink` files and TLS keys **out of the image context**. The GitHub asset digest verifies the downloaded archive; it is not an independent signature.

## 2. Import onto the selected node

Transfer the image tar to the node and independently compare its SHA-256. On k3s:

```bash
sudo k3s ctr --namespace k8s.io images import /path/to/eqlink-server-arm64-image.tar
sudo k3s crictl inspecti docker.io/eqlink/server:1.0.0-arm64
```

Substitute AMD64 names when appropriate. With another Kubernetes distribution using containerd, import into that node's actual CRI/containerd instance and `k8s.io` namespace; a Docker-engine import alone is insufficient. The generated Pod uses `imagePullPolicy: Never`, so the exact image reference must exist on its selected node. No image is pulled from the example `docker.io/eqlink/server` name.

## 3. Prepare and apply a new deployment

From the management machine with the cloned source, kubectl context and Python 3:

```bash
kubectl get nodes -o wide
kubectl get node YOUR_NODE -o jsonpath='{.status.allocatable}'
kubectl get storageclass
```

Check the node's architecture/Ready state, free TUN resource, Pod/Service/LAN/VPN subnet separation, and high-port availability. Put the certificate and key in a private directory outside the repository. Extract `xray` from the release server archive for the **management machine's** architecture; it only generates the initial REALITY key pair. If that machine has a different architecture from the target, use the other server archive for this tool.

```bash
umask 077
mkdir -p "$HOME/eqlink-private/tools"
tar -xzf /path/to/eqlink-server-arm64-1.0.0.tar.gz \
  -C "$HOME/eqlink-private/tools"
chmod 0755 "$HOME/eqlink-private/tools/xray"
python3 packaging/k3s/deploy.py \
  --node YOUR_NODE --arch arm64 \
  --image docker.io/eqlink/server:1.0.0-arm64 \
  --namespace eqlink-test --storage-class local-path \
  --public-host vpn.example.net --tls-server-name vpn.example.net \
  --tls-cert /private/tls.crt --tls-key /private/tls.key \
  --overlay 10.88.0.0/24 --tcp-port 30443 --udp-port 30444 \
  --tun-resource armhpc.home.arpa/tun \
  --xray "$HOME/eqlink-private/tools/xray" \
  --output "$HOME/eqlink-private/rendered" --render-only
```

Review the private generated manifest without sharing its Secret bodies. Then rerun the same command **without `--render-only`** to perform cluster preflight and apply. Do not run `kubectl apply` on the rendered manifest to bypass those preflight checks. `--render-only` does not access the cluster, so it does not prove its dependencies are available. The install command refuses an existing EqLink Deployment.

Forward only the outer ports to the pinned node or its existing VIP:

```text
Public TCP 30443 -> selected node/VIP:30443/TCP
Public UDP 30444 -> selected node/VIP:30444/UDP
```

The Service uses `externalTrafficPolicy: Local`; the destination must host the EqLink Pod. OpenVPN and control/auth/management listeners stay internal and receive no public Service ports.

## 4. Check and generate the reusable bundle

```bash
kubectl -n eqlink-test rollout status deployment/eqlink-server --timeout=300s
kubectl -n eqlink-test get pods -l app=eqlink-server -o wide
kubectl -n eqlink-test exec deployment/eqlink-server -c server -- EqLink.Server inspect --json
umask 077
python3 packaging/k3s/devices.py enrollment-create --namespace eqlink-test \
  --name network --output "$HOME/eqlink-private/network.eqlink"
```

For a private HY2 CA, include `--hysteria-ca /private/root-ca.cer --expected-ca-sha256 VERIFIED_CERTIFICATE_SHA256` in the enrollment command; see `devices.py --help` and the [trust tool](../packaging/k3s/bundle_trust.py). The verified certificate fingerprint is not a hash of the PEM file text. Transfer only the resulting bundle privately. Windows machines import the same file and receive separate identities and server-confirmed addresses.

Ready means the inner server and at least one outer path are available. Independently test real client traffic, both outer paths, peer access and failover; a Ready Pod is not end-to-end acceptance.

## Existing deployments and rollback

Keep your existing CA, REALITY keys, virtual subnet, VIP/NAT, PVC, enrolled identities and gateway extensions. **Do not rerun first-install initialization.** Build/import the new image and use `packaging/k3s/upgrade.py --help`; run its plan first and supply the exact image reference and verified CRI image ID when applying. The tool backs up private state and preserves startup wrappers, probes, mounts and gateway extensions. Review rollback material and retain an administrative path independent of EqLink. Legacy identity migration is documented in [migration-0.3.0.md](migration-0.3.0.md).

For the current ARMHPC environment, preserve `eqlink-test`, `armhpc-node10`, VIP `192.168.100.248`, virtual subnet `172.21.10.0/24` and the Rancher gateway. This guide does not modify or redeploy that environment.
