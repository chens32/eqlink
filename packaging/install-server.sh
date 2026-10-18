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
openvpn_bin=$(command -v openvpn)
cat >/etc/systemd/system/eqlink-openvpn.service <<EOF
[Unit]
Description=EqLink inner OpenVPN server
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
ExecStart=$openvpn_bin --config /etc/eqlink/openvpn.conf
Restart=on-failure
RestartSec=2
NoNewPrivileges=true
[Install]
WantedBy=multi-user.target
EOF
cat >/etc/systemd/system/eqlink-health.service <<EOF
[Unit]
Description=EqLink loopback health responder
After=network-online.target
[Service]
Type=simple
ExecStart=/usr/local/lib/eqlink/bin/EqLink.Server health --port $health_port
User=nobody
Group=nogroup
Restart=always
RestartSec=2
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
[Install]
WantedBy=multi-user.target
EOF
cat >/etc/systemd/system/eqlink-hysteria.service <<'EOF'
[Unit]
Description=EqLink Hysteria 2 outer server
After=network-online.target eqlink-openvpn.service eqlink-health.service
Wants=network-online.target
[Service]
Type=simple
ExecStart=/usr/local/lib/eqlink/bin/hysteria server -c /etc/eqlink/hysteria.json
Restart=always
RestartSec=2
NoNewPrivileges=true
[Install]
WantedBy=multi-user.target
EOF
cat >/etc/systemd/system/eqlink-xray.service <<'EOF'
[Unit]
Description=EqLink Xray outer server
After=network-online.target eqlink-openvpn.service eqlink-health.service
Wants=network-online.target
[Service]
Type=simple
ExecStart=/usr/local/lib/eqlink/bin/xray run -c /etc/eqlink/xray.json
Restart=always
RestartSec=2
NoNewPrivileges=true
[Install]
WantedBy=multi-user.target
EOF

cat >/etc/systemd/system/eqlink-crl-refresh.service <<'EOF'
[Unit]
Description=Refresh EqLink OpenVPN certificate revocation list
[Service]
Type=oneshot
ExecStart=/usr/local/bin/eqlink-server refresh-crl --root /etc/eqlink
NoNewPrivileges=true
EOF
cat >/etc/systemd/system/eqlink-crl-refresh.timer <<'EOF'
[Unit]
Description=Weekly EqLink CRL refresh
[Timer]
OnCalendar=weekly
Persistent=true
[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable --now eqlink-openvpn.service eqlink-health.service
systemctl enable --now eqlink-crl-refresh.timer
if [[ "$enable_udp" == true ]]; then systemctl enable --now eqlink-hysteria.service; else systemctl disable --now eqlink-hysteria.service 2>/dev/null || true; fi
if [[ "$enable_tcp" == true ]]; then systemctl enable --now eqlink-xray.service; else systemctl disable --now eqlink-xray.service 2>/dev/null || true; fi
echo "EqLink services installed. Allow only the required public outer ports: UDP/$udp_port ($enable_udp), TCP/$tcp_port ($enable_tcp)."
echo 'OpenVPN and health are bound to 127.0.0.1. Verify host/cloud firewall and external reachability before issuing device bundles.'
