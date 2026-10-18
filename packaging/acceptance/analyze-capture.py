#!/usr/bin/env python3
"""Read PktMon etl2txt IPv4 headers; component IDs must come from this capture's inventory."""
import argparse
import collections
import ipaddress
import json
import pathlib
import re

p=argparse.ArgumentParser(description=__doc__)
p.add_argument("text",type=pathlib.Path)
p.add_argument("--public-ip",required=True)
p.add_argument("--overlay",required=True)
p.add_argument("--physical-components",required=True,help="Comma-separated PktMon component IDs, not Windows interface indexes")
p.add_argument("--meta-components",default="",help="IDs from capture-components.txt; blank only when no competing TUN exists")
p.add_argument("--tcp-port",type=int,default=30443)
p.add_argument("--udp-port",type=int,default=30444)
p.add_argument("--openvpn-port",type=int,default=1194)
p.add_argument("--output",type=pathlib.Path,required=True)
a=p.parse_args()
physical={int(n) for n in a.physical_components.split(',') if n}
meta={int(n) for n in a.meta_components.split(',') if n}
if not physical or physical & meta: p.error("Provide distinct physical and TUN component IDs")
overlay=ipaddress.ip_network(a.overlay); ipaddress.ip_address(a.public_ip)
counts=collections.Counter(); parsed=collections.Counter(); lost=None; component=None; start=end=None
raw=a.text.read_bytes();encoding='utf-16' if raw.startswith((b'\xff\xfe',b'\xfe\xff')) else 'utf-8-sig'
for line in raw.decode(encoding,errors='replace').splitlines():
    if 'EventsLost:' in line:
        match=re.search(r'EventsLost:\s*(\d+)',line)
        if match: lost=int(match.group(1))
    if '[Microsoft-Windows-PktMon]' in line and 'PktGroupId' in line:
        match=re.search(r'(?:组件|Component)\s+(\d+)',line,re.I);component=int(match.group(1)) if match else None
        timestamp=re.search(r'::(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)',line)
        if timestamp: start=start or timestamp.group(1);end=timestamp.group(1)
        continue
    if component is None or not line.startswith('\t'): continue
    match=re.search(r'(\d+\.\d+\.\d+\.\d+)(?:\.(\d+))? > (\d+\.\d+\.\d+\.\d+)(?:\.(\d+))?: (.*)',line)
    if not match: continue
    src,sp,dst,dp,rest=match.groups();sp=int(sp) if sp else None;dp=int(dp) if dp else None
    parsed[str(component)]+=1
    outer=a.public_ip in (src,dst) and (rest.startswith('UDP') and a.udp_port in (sp,dp) or sp is not None and not rest.startswith('UDP') and a.tcp_port in (sp,dp))
    virtual=ipaddress.ip_address(src) in overlay or ipaddress.ip_address(dst) in overlay
    inner=a.public_ip in (src,dst) and a.openvpn_port in (sp,dp)
    if component in physical:
        if virtual: counts['virtualOnPhysical']+=1
        if inner: counts['directOpenVpnOnPhysical']+=1
        if outer: counts['outerOnPhysical']+=1
    if component in meta:
        if virtual: counts['virtualOnMeta']+=1
        if inner: counts['directOpenVpnOnMeta']+=1
        if outer: counts['outerOnMeta']+=1
violations=sum(counts[n] for n in ('virtualOnPhysical','directOpenVpnOnPhysical','virtualOnMeta','directOpenVpnOnMeta','outerOnMeta'))
result='failed' if violations else 'passed' if lost==0 and counts['outerOnPhysical']>0 else 'unavailable'
for name in ('virtualOnPhysical','directOpenVpnOnPhysical','virtualOnMeta','directOpenVpnOnMeta','outerOnMeta','outerOnPhysical'): counts.setdefault(name,0)
report={'schemaVersion':1,'result':result,'capture':a.text.name,'encoding':encoding,'started':start,'ended':end,'eventsLost':lost,
        'physicalComponents':sorted(physical),'metaComponents':sorted(meta),'parsedIpv4ByComponent':dict(parsed),'counts':dict(counts),
        'limits':'Header observations apply only to this capture window and these component mappings. No claim of outer fingerprint resistance. Missing outer traffic or loss metadata invalidates a zero-leak conclusion.'}
a.output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,ensure_ascii=False,indent=2))
raise SystemExit(0 if result=='passed' else 1)
