#!/usr/bin/env python3
"""Host-side download bridge for WSL builds; only Debian archive URLs are accepted."""
import json
import os
import pathlib
import sys
import urllib.error
import urllib.parse
import urllib.request

url, destination = sys.argv[1:]
parsed = urllib.parse.urlparse(url)
if parsed.scheme != "https" or parsed.hostname not in ("deb.debian.org", "security.debian.org"):
    raise SystemExit("Only official Debian HTTPS archives are allowed")
path = pathlib.Path(destination)
if not path.is_file():
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with urllib.request.urlopen(url, timeout=90) as response:
            temporary = pathlib.Path(str(path) + ".part")
            with temporary.open("wb") as output:
                while chunk := response.read(1024 * 1024):
                    output.write(chunk)
            os.replace(temporary, path)
    except urllib.error.HTTPError as error:
        print(json.dumps({"status": error.code}))
        raise SystemExit(0)
print(json.dumps({"status": 200, "length": path.stat().st_size}))
