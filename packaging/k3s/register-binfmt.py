#!/usr/bin/env python3
"""Register one temporary ARM64 interpreter; the caller removes its named entry."""
import pathlib
import re
import sys

name, executable = sys.argv[1:]
if not re.fullmatch(r"eqlink-arm64-[a-zA-Z0-9-]+", name):
    raise SystemExit("Invalid temporary binfmt name")
executable = str(pathlib.Path(executable).resolve(strict=True))
if ":" in executable or "\n" in executable:
    raise SystemExit("Invalid interpreter path")
magic = b"\x7fELF\x02\x01\x01\x00" + bytes(8) + b"\x02\x00\xb7\x00"
mask = b"\xff" * 7 + b"\x00" + b"\xff" * 8 + b"\xfe\xff\xff\xff"
with open("/proc/sys/fs/binfmt_misc/register", "wb", buffering=0) as stream:
    escaped = lambda value: "".join("\\x%02x" % byte for byte in value).encode()
    stream.write(b":" + name.encode() + b":M::" + escaped(magic) + b":" + escaped(mask) + b":" + executable.encode() + b":F")
