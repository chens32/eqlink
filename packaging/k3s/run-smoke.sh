#!/usr/bin/env bash
set -euo pipefail
repo=$(realpath -- "${1:?Repository path required}")
rootfs=$(cat "$repo/artifacts/k3s/rootfs-path.txt")
case "$rootfs" in /tmp/eqlink-k3s-build.*/rootfs) ;; *) echo 'Unsafe rootfs path' >&2; exit 1 ;; esac
task_root=$(dirname -- "$rootfs")
mode=${2:-full}
if [[ "$mode" == rpc-only ]]; then
    test_root=$(cat "$repo/artifacts/k3s/smoke-root-path.txt")
    case "$test_root" in /tmp/eqlink-k3s-smoke.*) ;; *) echo 'Unsafe test path' >&2; exit 1 ;; esac
else
    test_root=$(mktemp -d /tmp/eqlink-k3s-smoke.XXXXXX)
    printf '%s\n' "$test_root" > "$repo/artifacts/k3s/smoke-root-path.txt"
    mkdir -p "$test_root"/{data,runtime,bootstrap,tls,tmp/test-app}
    cp "$repo/artifacts/k3s/test-app/EqLink.Tests" "$test_root/tmp/test-app/EqLink.Tests"
    chmod 0755 "$test_root/tmp/test-app/EqLink.Tests"
fi
binfmt_name="eqlink-arm64-smoke-$$"
cleanup() {
    if [[ -e "/proc/sys/fs/binfmt_misc/$binfmt_name" ]]; then printf '%s\n' -1 > "/proc/sys/fs/binfmt_misc/$binfmt_name"; fi
}
trap cleanup EXIT
if [[ $(uname -m) != aarch64 ]]; then
    python3 "$repo/packaging/k3s/register-binfmt.py" "$binfmt_name" "$task_root/emulator/usr/bin/qemu-aarch64"
fi
tee_args=()
if [[ "$mode" == rpc-only ]]; then tee_args=(-a); fi
unshare --mount --net --pid --fork bash "$repo/packaging/k3s/smoke-inside.sh" "$rootfs" "$test_root" "$repo" "$mode" 2>&1 | tee "${tee_args[@]}" "$repo/artifacts/k3s/arm64-smoke.log"
