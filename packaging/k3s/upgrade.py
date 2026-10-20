#!/usr/bin/env python3
"""Plan by default; --apply backs up state and updates only the owned Pod fields.
Run from the remote management environment. Never emit Secret bodies or runtime credentials.
"""
import argparse
import copy
import hashlib
import json
import os
import pathlib
import re
import secrets
import sys
import time
from common import IMAGE, OWNER, Kube, private_write


def check_tun(kube, node, resource, namespace):
    if not re.fullmatch(r"[a-z0-9][a-z0-9.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*", resource):
        raise ValueError("Invalid TUN extended resource name")
    capacity = int(node["status"].get("allocatable", {}).get(resource, "0"))
    if capacity < 1:
        raise ValueError("Selected node has no allocatable " + resource + "; install its TUN device plugin first")
    pods = json.loads(kube.run("get", "pods", "-A", "--field-selector", "spec.nodeName=" + node["metadata"]["name"], "-o", "json"))["items"]
    occupied = 0
    for pod in pods:
        if pod["status"].get("phase") in ("Succeeded", "Failed"):
            continue
        if pod["metadata"].get("namespace") == namespace and pod["metadata"].get("labels", {}).get("app") == "eqlink-server":
            continue  # Recreate releases this instance before starting the new one.
        def request(container):
            return int(container.get("resources", {}).get("requests", {}).get(resource, "0"))
        apps = sum(request(c) for c in pod["spec"].get("containers", []))
        sidecars = peak = 0
        for init in pod["spec"].get("initContainers", []):
            if init.get("restartPolicy") == "Always":
                sidecars += request(init)
                peak = max(peak, sidecars)
            else:
                peak = max(peak, sidecars + request(init))
        occupied += max(apps + sidecars, peak) + int(pod["spec"].get("overhead", {}).get(resource, "0"))
    if occupied >= capacity:
        raise ValueError("TUN device resources are already allocated to another workload")


def upgraded_template(deployment, image, resource):
    template = copy.deepcopy(deployment["spec"]["template"])
    pod = template["spec"]
    candidates = [c for c in pod["containers"] if c["name"] == "server"]
    if len(candidates) != 1:
        raise ValueError("Expected exactly one server container")
    server = candidates[0]
    server["image"] = image
    server["imagePullPolicy"] = "Never"
    security = server.setdefault("securityContext", {})
    security.update(privileged=False, runAsUser=0, runAsGroup=0, readOnlyRootFilesystem=True,
                    allowPrivilegeEscalation=False, capabilities={"drop": ["ALL"], "add": ["NET_ADMIN"]}, seccompProfile={"type": "RuntimeDefault"})
    for field in ("requests", "limits"):
        values = server.setdefault("resources", {}).setdefault(field, {})
        other = [k for k in values if k.endswith("/tun") and k != resource]
        if other:
            raise ValueError("Deployment already uses a different TUN resource")
        values[resource] = "1"
    pod["automountServiceAccountToken"] = False
    legacy = {v["name"] for v in pod.get("volumes", []) if v.get("hostPath") == {"path": "/dev/net/tun", "type": "CharDevice"} and v["name"] == "tun"}
    if legacy:
        server["volumeMounts"] = [m for m in server.get("volumeMounts", []) if not (m["name"] in legacy and m["mountPath"] == "/dev/net/tun")]
        pod["volumes"] = [v for v in pod["volumes"] if v["name"] not in legacy]
    return template


def owned(deployment):
    if deployment["metadata"].get("labels", {}).get("app.kubernetes.io/managed-by") != OWNER:
        raise ValueError("Deployment is not owned by EqLink")
    if deployment["spec"].get("replicas") != 1 or deployment["spec"].get("strategy", {}).get("type") != "Recreate":
        raise ValueError("Upgrade requires one replica and Recreate")


def pod_for(kube, namespace):
    pods = json.loads(kube.run("get", "pods", "-n", namespace, "-l", "app=eqlink-server", "-o", "json"))["items"]
    pods = [p for p in pods if not p["metadata"].get("deletionTimestamp") and p["status"].get("phase") == "Running"]
    if len(pods) != 1:
        raise ValueError("Expected one running EqLink Pod")
    return pods[0]


def patch_template(kube, namespace, template, resource_version, dry_run=False):
    patch = [{"op": "test", "path": "/metadata/resourceVersion", "value": resource_version},
             {"op": "replace", "path": "/spec/template", "value": template}]
    args = ["patch", "deployment", "eqlink-server", "-n", namespace, "--type=json", "--patch-file", "-"]
    if dry_run:
        args += ["--dry-run=server"]
    kube.run(*args, input_data=json.dumps(patch).encode())


def wait_ready(kube, namespace, expected_id, timeout=180):
    until = time.monotonic() + timeout
    while time.monotonic() < until:
        try:
            pod = pod_for(kube, namespace)
            status = next(s for s in pod["status"].get("containerStatuses", []) if s["name"] == "server")
            if status.get("ready") and status.get("imageID", "").endswith(expected_id):
                return pod
        except (ValueError, StopIteration):
            pass
        time.sleep(3)
    raise RuntimeError("New Pod did not become Ready with the expected image ID")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--namespace", default="eqlink-test")
    parser.add_argument("--node", required=True)
    parser.add_argument("--arch", choices=("amd64", "arm64"), default="arm64")
    parser.add_argument("--image", help="Exact imported image reference; defaults to the bundled version for --arch")
    parser.add_argument("--tun-resource", default="armhpc.home.arpa/tun")
    parser.add_argument("--expected-image-id", help="sha256 ID reported by k3s crictl inspecti after verified offline import")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--rollback-from", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, default=pathlib.Path(__file__).parent / "private")
    parser.add_argument("--kubectl")
    parser.add_argument("--kubeconfig")
    args = parser.parse_args()
    args.image = args.image or IMAGE.rsplit("-", 1)[0] + "-" + args.arch
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/:@-]*", args.image):
        raise ValueError("Invalid container image reference")
    if os.name != "posix":
        raise ValueError("Run upgrade from a Linux management environment so private backups have POSIX permissions")
    for value in (args.namespace, args.node):
        if not re.fullmatch(r"[a-z0-9][a-z0-9.-]{0,252}", value):
            raise ValueError("Invalid namespace or node")
    if args.apply and not re.fullmatch(r"sha256:[0-9a-f]{64}", args.expected_image_id or ""):
        raise ValueError("--apply requires --expected-image-id from the imported image")
    kube = Kube(args.kubectl, args.kubeconfig)
    current = kube.get("deployment", "eqlink-server", args.namespace)
    owned(current)
    node = kube.get("node", args.node)
    if node["status"]["nodeInfo"]["architecture"] != args.arch or not any(c["type"] == "Ready" and c["status"] == "True" for c in node["status"].get("conditions", [])):
        raise ValueError("Selected node must be Ready " + args.arch)
    pinned = current["spec"]["template"]["spec"].get("affinity", {}).get("nodeAffinity", {}).get("requiredDuringSchedulingIgnoredDuringExecution", {}).get("nodeSelectorTerms", [])
    if len(pinned) != 1 or not any(f.get("key") == "metadata.name" and f.get("operator") == "In" and f.get("values") == [args.node] for f in pinned[0].get("matchFields", [])):
        raise ValueError("Deployment is not pinned to the requested node")
    check_tun(kube, node, args.tun_resource, args.namespace)
    pod = pod_for(kube, args.namespace)
    if not args.rollback_from:
        version = kube.run("exec", "-n", args.namespace, pod["metadata"]["name"], "-c", "server", "--", "python3", "-c", "import json;print(json.load(open('/etc/eqlink/state.json')).get('schemaVersion',1))").decode().strip()
        if version != "2":
            raise ValueError("Identity migration required. Use migrate-v2.py with independent management and an offline state backup before upgrading the image.")
    if pod["spec"]["nodeName"] != args.node:
        raise ValueError("Running Pod is on another node")
    if args.rollback_from:
        saved = json.loads((args.rollback_from / "deployment.json").read_text())
        owned(saved)
        if saved["metadata"].get("namespace") != args.namespace or saved["metadata"]["name"] != current["metadata"]["name"]:
            raise ValueError("Backup belongs to a different deployment or namespace")
        if saved["spec"]["template"]["spec"].get("affinity") != current["spec"]["template"]["spec"].get("affinity"):
            raise ValueError("Backup targets a different scheduling policy; manual review required")
        template = saved["spec"]["template"]
    else:
        template = upgraded_template(current, args.image, args.tun_resource)
    patch_template(kube, args.namespace, template, current["metadata"]["resourceVersion"], dry_run=True)
    folder = args.output / ("upgrade-" + time.strftime("%Y%m%d-%H%M%S") + "-" + secrets.token_hex(3))
    folder.mkdir(parents=True, mode=0o700)
    if os.name != "nt":
        os.chmod(folder, 0o700)
    private_write(folder / "deployment.json", json.dumps(current).encode())
    private_write(folder / "planned-template.json", json.dumps(template).encode())
    old_container = next(c for c in current["spec"]["template"]["spec"]["containers"] if c["name"] == "server")
    new_container = next(c for c in template["spec"]["containers"] if c["name"] == "server")
    public = {"namespace": args.namespace, "node": args.node, "oldImage": old_container["image"], "newImage": new_container["image"],
              "oldSecurity": old_container.get("securityContext"), "newSecurity": new_container.get("securityContext"),
              "commandArgsPreserved": old_container.get("command") == new_container.get("command") and old_container.get("args") == new_container.get("args"),
              "probesPreserved": all(old_container.get(p) == new_container.get(p) for p in ("startupProbe", "readinessProbe", "livenessProbe")),
              "secretAndPersistentStateChanged": False, "applied": False}
    private_write(folder / "diff-public.json", json.dumps(public, indent=2).encode())
    print(json.dumps(public, indent=2))
    if not args.apply:
        print("Private plan directory: " + str(folder))
        return
    # Backup the exact mounted objects before changing the deployment. Never print their bodies.
    for kind, names in (("secret", {"eqlink-bootstrap", "eqlink-tls"}), ("configmap", {v["configMap"]["name"] for v in pod["spec"].get("volumes", []) if "configMap" in v})):
        for name in sorted(names):
            private_write(folder / (kind + "-" + name + ".json"), json.dumps(kube.get(kind, name, args.namespace)).encode())
    for kind, name in (("service", "eqlink-outer"), ("pvc", "eqlink-data")):
        private_write(folder / (kind + ".json"), json.dumps(kube.get(kind, name, args.namespace)).encode())
    remote_backup = "/tmp/eqlink-backup-" + secrets.token_hex(12) + ".tar.gz"
    # Python is part of the existing offline image. Its flock matches FileShare.None on Linux.
    backup_code = "import fcntl,os,subprocess,sys; p=sys.argv[1]; f=open('/etc/eqlink/.state.lock','a'); fcntl.flock(f,fcntl.LOCK_EX); fd=os.open(p,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600); os.close(fd); subprocess.run(['tar','--exclude=./.state.lock','-C','/etc/eqlink','-czf',p,'.'],check=True)"
    prefix = ["exec", "-n", args.namespace, pod["metadata"]["name"], "-c", "server", "--"]
    kube.run(*prefix, "python3", "-c", backup_code, remote_backup)
    try:
        data = kube.run(*prefix, "cat", remote_backup)
        private_write(folder / "state.tar.gz", data)
        private_write(folder / "state.sha256", hashlib.sha256(data).hexdigest().encode())
    finally:
        kube.run(*prefix, "rm", "--", remote_backup)
    # A maintenance window excludes concurrent device add/revoke until this rollout completes.
    latest = kube.get("deployment", "eqlink-server", args.namespace)
    if latest["metadata"]["resourceVersion"] != current["metadata"]["resourceVersion"]:
        raise RuntimeError("Deployment changed during backup; nothing applied")
    old_status = next(s for s in pod["status"]["containerStatuses"] if s["name"] == "server")
    try:
        patch_template(kube, args.namespace, template, current["metadata"]["resourceVersion"])
        running = wait_ready(kube, args.namespace, args.expected_image_id)
        try:
            inspection = json.loads(kube.run("exec", "-n", args.namespace, running["metadata"]["name"], "-c", "server", "--", "/usr/local/bin/EqLink.Server", "inspect", "--json"))
        except RuntimeError:
            if not args.rollback_from:
                raise
            inspection = {"result": "unavailable", "reason": "Saved legacy server has no inspect command; validate original probes and virtual communication manually"}
        private_write(folder / "inspection.json", json.dumps(inspection, indent=2).encode())
        if "checks" in inspection and not all(inspection["checks"].values()):
            raise RuntimeError("Runtime inspection did not match the required invariants")
        public["runtimeInspection"] = "passed" if "checks" in inspection else "unavailable"
        public["applied"] = True
        private_write(folder / "result.json", json.dumps(public, indent=2).encode())
        print("Upgrade verified; client-side public-network acceptance is still required. Backup: " + str(folder))
    except Exception:
        latest = kube.get("deployment", "eqlink-server", args.namespace)
        if latest["spec"]["template"] != template:
            raise RuntimeError("Rollout failed and deployment changed externally; backup retained, automatic rollback withheld")
        patch_template(kube, args.namespace, current["spec"]["template"], latest["metadata"]["resourceVersion"])
        wait_ready(kube, args.namespace, old_status["imageID"])
        raise RuntimeError("Upgrade failed; original Pod template restored. Private backup retained: " + str(folder))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
    except (OSError, KeyError, StopIteration):
        print("Upgrade unavailable or failed. Check RBAC, TUN capacity, private backup, and rollout status.", file=sys.stderr)
        sys.exit(1)
