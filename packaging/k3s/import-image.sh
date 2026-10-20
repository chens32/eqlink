#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
if [[ $(id -u) -ne 0 ]]; then echo 'Run as root on the selected ARM64 k3s node.' >&2; exit 1; fi
if [[ $(uname -m) != aarch64 ]]; then echo 'This package requires an ARM64 node.' >&2; exit 1; fi
command -v k3s >/dev/null
sha256sum -c SHA256SUMS
test -c /dev/net/tun || { echo '/dev/net/tun is missing; enable the TUN driver on this node.' >&2; exit 1; }
k3s ctr --namespace k8s.io images import images/eqlink-server-arm64.tar
k3s ctr --namespace k8s.io images list | awk '/eqlink\/server/ { print }'
echo 'Image imported on this node. Inspect the CRI image ID:'
k3s crictl inspecti docker.io/eqlink/server:1.0.0-arm64 | python3 -c 'import json,sys; print(json.load(sys.stdin)["status"]["id"])'
echo 'Use deploy.py for a new installation; use upgrade.py for an existing deployment. Keep the original image for rollback.'
