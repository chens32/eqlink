#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ! -f "$1" ]]; then
    echo 'Usage: sudo bash install-server.sh /path/to/server-settings.json' >&2
    exit 2
fi
if [[ $(id -u) -ne 0 ]]; then
    echo 'Run as root.' >&2
    exit 2
fi
settings=$(realpath -- "$1")
package_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
chmod 0755 "$package_dir/EqLink.Server" "$package_dir/hysteria" "$package_dir/xray"
export PATH="$package_dir:$PATH"
for tool in openvpn openssl ip python3 systemctl; do
    command -v "$tool" >/dev/null || { echo "Missing $tool" >&2; exit 1; }
done
version=$(openvpn --version | sed -nE '1s/.* ([0-9]+)\.([0-9]+)\..*/\1 \2/p')
read -r major minor <<< "$version"
if [[ -z "${major:-}" || $major -lt 2 || ( $major -eq 2 && $minor -lt 6 ) ]]; then
    echo 'OpenVPN 2.6 or newer is required.' >&2
    exit 1
fi

if [[ -f /etc/eqlink/state.json ]]; then
    python3 - /etc/eqlink/state.json <<'PY'
import json, sys
state = json.load(open(sys.argv[1], encoding='utf-8'))
if state.get('schemaVersion', 1) != 2:
    raise SystemExit('Migration required: stop legacy services and use EqLink.Server migrate-v2 with an external private backup before upgrading.')
PY
fi

if [[ ! -f /etc/eqlink/state.json ]]; then
    "$package_dir/EqLink.Server" preflight --settings "$settings" --root /etc/eqlink
fi

install -d -m 0755 /usr/local/lib/eqlink/bin
install -m 0755 "$package_dir/EqLink.Server" /usr/local/lib/eqlink/bin/EqLink.Server
install -m 0755 "$package_dir/hysteria" /usr/local/lib/eqlink/bin/hysteria
install -m 0755 "$package_dir/xray" /usr/local/lib/eqlink/bin/xray
install -d -m 0755 /usr/local/lib/eqlink/licenses
install -m 0644 "$package_dir/hysteria-LICENSE.md" /usr/local/lib/eqlink/licenses/hysteria-LICENSE.md
install -m 0644 "$package_dir/xray-LICENSE" /usr/local/lib/eqlink/licenses/xray-LICENSE
cp -a "$package_dir/licenses/." /usr/local/lib/eqlink/licenses/
cat >/usr/local/bin/eqlink-server <<'EOF'
#!/bin/sh
PATH=/usr/local/lib/eqlink/bin:$PATH
export PATH
exec /usr/local/lib/eqlink/bin/EqLink.Server "$@"
EOF
chmod 0755 /usr/local/bin/eqlink-server

if [[ ! -f /etc/eqlink/state.json ]]; then
    eqlink-server init --settings "$settings" --root /etc/eqlink
else
    eqlink-server apply --root /etc/eqlink
fi

read -r health_port udp_port tcp_port enable_udp enable_tcp < <(python3 - /etc/eqlink/state.json <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding='utf-8'))
s = d['settings']
print(s.get('healthPort', 1195), s.get('udpPort', 443), s.get('tcpPort', 443),
      str(s.get('enableUdp', True)).lower(), str(s.get('enableTcp', True)).lower())
PY
)
# A single supervisor owns the authenticated control host and all three data processes.
# Stop only EqLink's legacy units. Unrelated OpenVPN services/configuration are untouched.
systemctl disable --now eqlink-openvpn.service eqlink-health.service eqlink-hysteria.service eqlink-xray.service eqlink-crl-refresh.timer 2>/dev/null || true
install -d -m 0700 /run/eqlink
install -d -m 0700 /etc/eqlink/bootstrap
if [[ "$settings" != /etc/eqlink/bootstrap/server-settings.json ]]; then install -m 0600 "$settings" /etc/eqlink/bootstrap/server-settings.json; fi
bound_caps='CAP_NET_ADMIN'
if [[ "$enable_tcp" == true && "$tcp_port" -lt 1024 || "$enable_udp" == true && "$udp_port" -lt 1024 ]]; then bound_caps+=' CAP_NET_BIND_SERVICE'; fi
cat >/etc/systemd/system/eqlink.service <<EOF
[Unit]
Description=EqLink private network supervisor
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
Environment=PATH=/usr/local/lib/eqlink/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/run/eqlink/dotnet
ExecStart=/usr/local/lib/eqlink/bin/EqLink.Server serve --settings /etc/eqlink/bootstrap/server-settings.json
Restart=on-failure
RestartSec=3
RuntimeDirectory=eqlink
RuntimeDirectoryMode=0700
KillMode=control-group
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/etc/eqlink /run/eqlink
PrivateTmp=true
CapabilityBoundingSet=$bound_caps
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now eqlink.service
echo "EqLink installed. Allow only UDP/$udp_port ($enable_udp), TCP/$tcp_port ($enable_tcp) on the public interface."
echo 'Control, authentication, management and stats are loopback only. Inner TLS health also listens on the virtual server address.'
