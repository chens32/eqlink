#!/usr/bin/env python3
"""Shared helpers for the offline test package. Never print Secret responses."""
import json
import os
import pathlib
import shutil
import subprocess

IMAGE = "docker.io/eqlink/server:1.0.0-arm64"
OWNER = "eqlink-test-packer"


def private_write(path, data):
    path = pathlib.Path(path)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(data)


class Kube:
    def __init__(self, executable=None, kubeconfig=None):
        if executable:
            self.command = [executable]
        elif shutil.which("k3s"):
            self.command = ["k3s", "kubectl"]
        else:
            self.command = ["kubectl"]
        if kubeconfig:
            self.command += ["--kubeconfig", str(pathlib.Path(kubeconfig).resolve())]

    def run(self, *args, input_data=None, timeout=60):
        result = subprocess.run(self.command + list(args), input=input_data, capture_output=True, timeout=timeout)
        if result.returncode:
            raise RuntimeError("kubectl failed (exit %d); raw response withheld to protect credentials" % result.returncode)
        return result.stdout

    def get(self, resource, name=None, namespace=None, optional=False):
        args = ["get", resource]
        if name:
            args.append(name)
        if namespace:
            args += ["-n", namespace]
        if optional:
            args += ["--ignore-not-found"]
        args += ["-o", "json"]
        data = self.run(*args)
        return json.loads(data) if data.strip() else None

    def apply(self, document, dry_run=False):
        args = ["apply", "-f", "-"]
        if dry_run:
            args += ["--dry-run=server"]
        self.run(*args, input_data=json.dumps(document).encode())
