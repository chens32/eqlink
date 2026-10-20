#!/usr/bin/env python3
"""OpenSSL/fixture tests; no cluster, deployed credentials or OS trust changes."""
import argparse
import base64
import contextlib
import hashlib
import io
import json
import pathlib
import secrets
import shutil
import subprocess
import tempfile
import uuid
from bundle_trust import add_hysteria_ca, exported_bundle


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--openssl", default=shutil.which("openssl"))
    args = parser.parse_args()
    if not args.openssl:
        raise SystemExit("Provide --openssl")
    with tempfile.TemporaryDirectory(prefix="eqlink-ca-tool-fixture-") as folder:
        root = pathlib.Path(folder)
        def run(*values):
            result = subprocess.run([args.openssl, *map(str, values)], capture_output=True, timeout=20)
            assert result.returncode == 0, "Fixture certificate generation failed: " + result.stderr.decode(errors="replace")[-1600:]
            return result.stdout
        for name in ("ca", "other"):
            run("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", root / (name + ".key"), "-out", root / (name + ".pem"), "-days", "2", "-subj", "/CN=EqLink-isolated-" + name, "-addext", "basicConstraints=critical,CA:TRUE", "-addext", "keyUsage=critical,keyCertSign,cRLSign")
        run("req", "-newkey", "rsa:2048", "-nodes", "-keyout", root / "server.key", "-out", root / "server.csr", "-subj", "/CN=localhost")
        (root / "server.ext").write_text("subjectAltName=DNS:localhost\nbasicConstraints=critical,CA:FALSE\nextendedKeyUsage=serverAuth\n")
        run("x509", "-req", "-in", root / "server.csr", "-CA", root / "ca.pem", "-CAkey", root / "ca.key", "-set_serial", "1", "-days", "1", "-out", root / "server.pem", "-extfile", root / "server.ext")
        der = run("x509", "-in", root / "ca.pem", "-outform", "DER")
        (root / "ca.cer").write_bytes(der)
        fingerprint = hashlib.sha256(der).hexdigest()
        pin = hashlib.sha256(run("x509", "-in", root / "server.pem", "-outform", "DER")).hexdigest()
        grant = uuid.uuid4().hex
        bundle = {"schemaVersion": 2, "grantId": grant, "enrollmentToken": secrets.token_hex(32), "hysteriaAuth": grant + ":" + secrets.token_hex(32), "vlessId": str(uuid.uuid4()), "network": {"networkId": uuid.uuid4().hex, "publicHost": "localhost", "overlayCidr": "172.30.251.0/24", "enableUdp": True, "enableTcp": False, "caPem": (root / "ca.pem").read_text(), "hysteriaServerName": "localhost", "hysteriaCertificateSha256": pin}}
        metadata = {"networkId": bundle["network"]["networkId"], "grantActive": True, "certificatePem": (root / "server.pem").read_text()}
        class FakeKube:
            def run(self, *arguments):
                text = " ".join(arguments)
                assert bundle["enrollmentToken"] not in text and bundle["hysteriaAuth"] not in text and bundle["vlessId"] not in text
                assert arguments[0] == "exec"
                if "/etc/eqlink/exports" in text:
                    return json.dumps({"ok": True, "bundle": base64.b64encode(json.dumps(bundle).encode()).decode()}).encode()
                return json.dumps(metadata).encode()
        source = json.dumps(bundle).encode()
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            assert json.loads(exported_bundle(FakeKube(), "fixture", bundle["grantId"])) == bundle
            first = add_hysteria_ca(FakeKube(), "fixture", source, root / "ca.cer", fingerprint, args.openssl)
            repeated = add_hysteria_ca(FakeKube(), "fixture", first, root / "ca.pem", fingerprint, args.openssl)
        assert not output.getvalue(), "Credentials or PEM were printed"
        assert first == repeated, "Adding the same CA is not idempotent"
        result = json.loads(first)
        embedded = result["network"].pop("hysteriaCaPem")
        assert result == bundle and embedded.replace("\r\n", "\n") == (root / "ca.pem").read_text()
        print("DER/PEM CA attachment, credential preservation, idempotence and output privacy: passed")
        def reject(data, ca, expected):
            try:
                add_hysteria_ca(FakeKube(), "fixture", json.dumps(data).encode(), ca, expected, args.openssl)
                raise AssertionError("Invalid CA, grant, pin or hostname was accepted")
            except ValueError:
                pass
        reject(bundle, root / "ca.cer", "0" * 64)
        other_fingerprint = hashlib.sha256(run("x509", "-in", root / "other.pem", "-outform", "DER")).hexdigest()
        reject(bundle, root / "other.pem", other_fingerprint)
        wrong_pin = json.loads(source); wrong_pin["network"]["hysteriaCertificateSha256"] = "0" * 64
        reject(wrong_pin, root / "ca.cer", fingerprint)
        wrong_host = json.loads(source); wrong_host["network"]["hysteriaServerName"] = "wrong.test.invalid"
        reject(wrong_host, root / "ca.cer", fingerprint)
        metadata["grantActive"] = False
        reject(bundle, root / "ca.cer", fingerprint)
        metadata["grantActive"] = True; metadata["networkId"] = uuid.uuid4().hex
        reject(bundle, root / "ca.cer", fingerprint)
        print("Wrong fingerprint/issuer/pin/hostname, revoked grant and wrong network: rejected")


if __name__ == "__main__":
    main()
