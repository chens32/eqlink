#!/usr/bin/env bash
set -euo pipefail
rootfs=$1
test_root=$2
repo=$3
mode=$4
mount --make-rprivate /
mount --bind "$rootfs" "$rootfs"
mount -o remount,bind,ro "$rootfs"
mount --bind "$test_root/data" "$rootfs/etc/eqlink"
mount --bind "$test_root/runtime" "$rootfs/run/eqlink"
mount --bind "$test_root/bootstrap" "$rootfs/run/eqlink-bootstrap"
mount --bind "$test_root/tls" "$rootfs/run/eqlink-tls"
mount --bind "$test_root/tmp" "$rootfs/tmp"
mount --bind /dev "$rootfs/dev"
mount -t proc proc "$rootfs/proc"
ip link set lo up
if [[ "$mode" != rpc-only ]]; then
    chroot "$rootfs" env PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin EQLINK_RUNTIME=container DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net /tmp/test-app/EqLink.Tests --container-smoke
fi
python3 "$repo/packaging/k3s/check-device-rpc.py" "$rootfs"
