#!/usr/bin/env python3
"""Loopback-only build mirror. Debian signatures/checksums remain checked by APT."""
import hashlib
import http.server
import json
import pathlib
import shutil
import subprocess
import sys
import threading

repo, windows_python, port_file = sys.argv[1:]
repo = pathlib.Path(repo).resolve()
cache = repo / "artifacts/k3s/mirror-cache"
cache.mkdir(parents=True, exist_ok=True)
locks = {}
guard = threading.Lock()


def windows_path(path):
    return subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()


helper = windows_path(repo / "packaging/k3s/fetch-build-url.py")


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/debian/"):
            url = "https://deb.debian.org" + self.path
        elif self.path.startswith("/security/"):
            url = "https://security.debian.org/debian-security/" + self.path[len("/security/"):]
        else:
            self.send_error(404)
            return
        if ".." in self.path or "\n" in self.path:
            self.send_error(400)
            return
        target = cache / hashlib.sha256(url.encode()).hexdigest()
        with guard:
            lock = locks.setdefault(str(target), threading.Lock())
        try:
            with lock:
                result = subprocess.run([windows_python, helper, url, windows_path(target)], capture_output=True, timeout=100)
                if result.returncode:
                    raise RuntimeError("Host HTTPS download failed")
                metadata = json.loads(result.stdout)
                if metadata["status"] != 200:
                    self.send_error(metadata["status"])
                    return
            self.send_response(200)
            self.send_header("Content-Length", str(target.stat().st_size))
            self.end_headers()
            with target.open("rb") as source:
                shutil.copyfileobj(source, self.wfile)
        except Exception as error:
            self.send_error(502, str(error))

    def log_message(self, *args):
        pass


server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
pathlib.Path(port_file).write_text(str(server.server_port))
server.serve_forever()
