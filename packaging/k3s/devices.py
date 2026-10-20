#!/usr/bin/env python3
"""Export reusable enrollment bundles and manage independent enrolled devices."""
import argparse
import base64
import json
import pathlib
import subprocess
import sys
import uuid

from common import Kube, private_write
from bundle_trust import add_hysteria_ca, exported_bundle


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("enrollment-create", "enrollment-list", "enrollment-revoke", "bundle-add-ca", "add", "list", "revoke", "status"))
    parser.add_argument("--namespace", default="eqlink-test")
    parser.add_argument("--kubectl")
    parser.add_argument("--kubeconfig")
    parser.add_argument("--name")
    parser.add_argument("--output", type=pathlib.Path)
    parser.add_argument("--id")
    parser.add_argument("--input", type=pathlib.Path)
    parser.add_argument("--hysteria-ca", type=pathlib.Path)
    parser.add_argument("--expected-ca-sha256")
    parser.add_argument("--openssl")
    args = parser.parse_args()
    if bool(args.hysteria_ca) != bool(args.expected_ca_sha256):
        parser.error("--hysteria-ca and --expected-ca-sha256 must be provided together")
    kube = Kube(args.kubectl, args.kubeconfig)
    remote = ["exec", "-n", args.namespace, "deployment/eqlink-server", "-c", "server", "--", "EqLink.Server"]
    if args.command == "bundle-add-ca":
        if bool(args.input) == bool(args.id) or not args.output or not args.hysteria_ca:
            parser.error("bundle-add-ca requires exactly one of --input/--id, plus --output, --hysteria-ca and --expected-ca-sha256")
        if args.output.exists():
            raise ValueError("Output already exists")
        source = args.input.read_bytes() if args.input else exported_bundle(kube, args.namespace, args.id)
        data = add_hysteria_ca(kube, args.namespace, source, args.hysteria_ca, args.expected_ca_sha256, args.openssl)
        private_write(args.output, data)
        print("Universal bundle updated with verified process-scoped CA; existing grant and credentials preserved: " + str(args.output))
    elif args.command in ("list", "enrollment-list"):
        print(kube.run(*remote, args.command).decode(), end="")
    elif args.command == "status":
        print(kube.run(*remote, "probe", "--kind", "status").decode(), end="")
    elif args.command in ("revoke", "enrollment-revoke"):
        if not args.id:
            parser.error("revoke requires --id")
        print(kube.run(*remote, args.command, "--id", uuid.UUID(args.id).hex, timeout=120).decode(), end="")
    else:
        if not args.name or not args.output:
            parser.error("add requires --name and --output")
        if args.output.exists():
            raise ValueError("Output already exists")
        operation_file = pathlib.Path(str(args.output) + ".request.json")
        if operation_file.exists():
            operation = json.loads(operation_file.read_text())
            if operation["name"] != args.name:
                raise ValueError("The saved operation belongs to another device name")
        else:
            operation = {"operationId": uuid.uuid4().hex, "name": args.name}
            private_write(operation_file, json.dumps(operation).encode())
        print("Operation ID: " + operation["operationId"] + ". Preserve the request file if the connection fails.")
        request = dict(operation)
        data = kube.run("exec", "-i", "-n", args.namespace, "deployment/eqlink-server", "-c", "server", "--", "python3", "/usr/local/lib/eqlink/device-request.py", input_data=json.dumps(request).encode(), timeout=150)
        response = json.loads(data)
        if not response.get("ok"):
            raise RuntimeError(response.get("error", "Device request failed"))
        bundle_bytes = base64.b64decode(response["bundle"])
        if args.hysteria_ca:
            bundle_bytes = add_hysteria_ca(kube, args.namespace, bundle_bytes, args.hysteria_ca, args.expected_ca_sha256, args.openssl)
        private_write(args.output, bundle_bytes)
        print(response["message"])
        print("Universal enrollment bundle: " + str(args.output))


if __name__ == "__main__":
    try:
        main()
    except subprocess.TimeoutExpired:
        print("Device operation outcome is unknown. Keep the .request.json file, inspect devices.py list, and retry the same operation ID without changing the operation ID.", file=sys.stderr)
        sys.exit(1)
    except (ValueError, RuntimeError, OSError, KeyError) as exc:
        print("Device operation failed: " + str(exc), file=sys.stderr)
        sys.exit(1)
