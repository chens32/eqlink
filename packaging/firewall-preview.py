#!/usr/bin/env python3
"""Print a dedicated nftables input table for a single-purpose EqLink server.

Review the output with the host administrator before applying it. It requires
an explicit management source and port so a remote SSH session is not guessed.
"""

import argparse
import ipaddress
import json
import pathlib


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("state", type=pathlib.Path, help="EqLink state.json")
    parser.add_argument("--admin-cidr", required=True, help="Management source IPv4/IPv6 CIDR")
    parser.add_argument("--admin-tcp-port", required=True, type=int, help="SSH or other management TCP port")
    args = parser.parse_args()
    admin = ipaddress.ip_network(args.admin_cidr, strict=False)
    if not 1 <= args.admin_tcp_port <= 65535:
        parser.error("management port must be in 1..65535")
    settings = json.loads(args.state.read_text(encoding="utf-8"))["settings"]
    if not settings.get("enableUdp", True) and not settings.get("enableTcp", True):
        parser.error("EqLink state has no enabled outer path")
    source = "ip" if admin.version == 4 else "ip6"
    print("table inet eqlink_input {")
    print("  chain input {")
    print("    type filter hook input priority 0; policy drop;")
    print("    iifname \"lo\" accept")
    print("    ct state established,related accept")
    print("    iifname \"tun*\" accept")
    print("    ip protocol icmp accept")
    print("    ip6 nexthdr icmpv6 accept")
    print(f"    {source} saddr {admin} tcp dport {args.admin_tcp_port} accept")
    if settings.get("enableUdp", True):
        print(f"    udp dport {int(settings.get('udpPort', 443))} accept")
    if settings.get("enableTcp", True):
        print(f"    tcp dport {int(settings.get('tcpPort', 443))} accept")
    print("  }")
    print("}")


if __name__ == "__main__":
    main()
