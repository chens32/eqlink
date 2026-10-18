#!/usr/bin/env python3
"""Issue, list, or revoke test devices without passing passwords in command arguments."""
import argparse
import base64
import getpass
import json
import pathlib
import subprocess
import sys
import uuid

from common import Kube, private_write


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("add", "list", "revoke", "status"))
    parser.add_argument("--namespace", default="eqlink-test")
    parser.add_argument("--kubectl")
    parser.add_argument("--kubeconfig")
    parser.add_argument("--name")
    parser.add_argument("--output", type=pathlib.Path)
    parser.add_argument("--id")
    args = parser.parse_args()
    kube = Kube(args.kubectl, args.kubeconfig)
    remote = ["exec", "-n", args.namespace, "deployment/eqlink-server", "-c", "server", "--", "EqLink.Server"]
    if args.command == "list":
        print(kube.run(*remote, "list").decode(), end="")
    elif args.command == "status":
        print(kube.run(*remote, "probe", "--kind", "status").decode(), end="")
    elif args.command == "revoke":
        if not args.id:
            parser.error("revoke requires --id")
        print(kube.run(*remote, "revoke", "--id", uuid.UUID(args.id).hex, timeout=120).decode(), end="")
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
        password = getpass.getpass("Bundle passphrase (12+ characters): ")
        if len(password) < 12 or password != getpass.getpass("Confirm passphrase: "):
            raise ValueError("Passphrases differ or are too short")
        print("Operation ID: " + operation["operationId"] + ". Preserve the request file if the connection fails.")
        request = dict(operation, passphrase=password)
        data = kube.run("exec", "-i", "-n", args.namespace, "deployment/eqlink-server", "-c", "server", "--", "python3", "/usr/local/lib/eqlink/device-request.py", input_data=json.dumps(request).encode(), timeout=150)
        response = json.loads(data)
        if not response.get("ok"):
            raise RuntimeError(response.get("error", "Device request failed"))
        private_write(args.output, base64.b64decode(response["bundle"]))
        print(response["message"])
        print("Encrypted import bundle: " + str(args.output))


if __name__ == "__main__":
    try:
        main()
    except subprocess.TimeoutExpired:
        print("Device operation outcome is unknown. Keep the .request.json file, inspect devices.py list, and retry the same operation ID with the original password.", file=sys.stderr)
        sys.exit(1)
    except (ValueError, RuntimeError, OSError, KeyError) as exc:
        print("Device operation failed: " + str(exc), file=sys.stderr)
        sys.exit(1)
