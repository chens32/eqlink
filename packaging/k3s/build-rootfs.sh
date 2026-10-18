#!/usr/bin/env bash
set -euo pipefail
# Run as root in a Linux build environment with debootstrap and Debian archive keys.
repo=$(realpath -- "${1:?Usage: build-rootfs.sh /path/to/repository}")
out="$repo/artifacts/k3s"
mkdir -p "$out/build-tools"
task_root=$(mktemp -d /tmp/eqlink-k3s-build.XXXXXX)
rootfs="$task_root/rootfs"
mkdir -p "$rootfs"
printf '%s\n' "$task_root" > "$out/build-root-path.txt"
binfmt_name="eqlink-arm64-$$"
bridge_pid=''
mirror='https://deb.debian.org/debian'
security_mirror='https://security.debian.org/debian-security'
cleanup_binfmt() {
    if [[ -e "/proc/sys/fs/binfmt_misc/$binfmt_name" ]]; then
        printf '%s\n' -1 > "/proc/sys/fs/binfmt_misc/$binfmt_name"
    fi
    if [[ -n "$bridge_pid" ]]; then kill "$bridge_pid" 2>/dev/null || true; fi
}
trap cleanup_binfmt EXIT
if [[ -n "${EQLINK_WINDOWS_PYTHON:-}" ]]; then
    python3 "$repo/packaging/k3s/wsl-download-bridge.py" "$repo" "$EQLINK_WINDOWS_PYTHON" "$task_root/mirror-port" &
    bridge_pid=$!
    for ((i=0; i<100; i++)); do [[ -f "$task_root/mirror-port" ]] && break; sleep 0.1; done
    mirror="http://127.0.0.1:$(cat "$task_root/mirror-port")/debian"
    security_mirror="http://127.0.0.1:$(cat "$task_root/mirror-port")/security"
fi
if [[ $(uname -m) != aarch64 ]]; then
    cd "$out/build-tools"
    if [[ ! -f qemu-user_10.0.13+ds-0+deb13u1_amd64.deb ]]; then
        if [[ -n "${EQLINK_WINDOWS_PYTHON:-}" ]]; then
            curl --fail "$mirror/pool/main/q/qemu/qemu-user_10.0.13%2bds-0%2bdeb13u1_amd64.deb" -o qemu-user_10.0.13+ds-0+deb13u1_amd64.deb
        else
            apt-get download 'qemu-user=1:10.0.13+ds-0+deb13u1'
        fi
    fi
    printf '%s\n' 'ca6ede739327a20ae5a3498230c8a3a9a072c586e131bc6bcc1201eb1725e336  qemu-user_10.0.13+ds-0+deb13u1_amd64.deb' | sha256sum -c -
    mkdir -p "$task_root/emulator"
    dpkg-deb --fsys-tarfile qemu-user_10.0.13+ds-0+deb13u1_amd64.deb | tar -x -C "$task_root/emulator" ./usr/bin/qemu-aarch64
    python3 "$repo/packaging/k3s/register-binfmt.py" "$binfmt_name" "$task_root/emulator/usr/bin/qemu-aarch64"
fi
debootstrap --variant=minbase --arch=arm64 --include=ca-certificates --foreign bookworm "$rootfs" "$mirror"
printf '#!/bin/sh\nexit 101\n' > "$rootfs/usr/sbin/policy-rc.d"
chmod 0755 "$rootfs/usr/sbin/policy-rc.d"
chroot "$rootfs" /debootstrap/debootstrap --second-stage
printf '%s\n' \
    "deb $mirror bookworm main" \
    "deb $mirror bookworm-updates main" \
    "deb $security_mirror bookworm-security main" > "$rootfs/etc/apt/sources.list"
chroot "$rootfs" apt-get update
chroot "$rootfs" env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    openvpn openssl iproute2 python3 iperf3 procps libicu72 libgssapi-krb5-2 libstdc++6
chroot "$rootfs" dpkg-query -W > "$out/debian-packages-arm64.txt"
chroot "$rootfs" apt-get clean
find "$rootfs/var/lib/apt/lists" -type f -delete
printf '%s\n' "$rootfs" > "$out/rootfs-path.txt"
printf 'Prepared ARM64 rootfs: %s\n' "$rootfs"
