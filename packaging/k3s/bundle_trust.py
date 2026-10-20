"""Add public, process-scoped Hysteria trust to a v2 bundle without changing its grant."""
import hashlib
import base64
import json
import pathlib
import re
import shutil
import subprocess
import tempfile
import uuid


def exported_bundle(kube, namespace, grant_id):
    grant = uuid.UUID(grant_id).hex
    query = r"""import base64,json,pathlib,sys
for path in sorted(pathlib.Path('/etc/eqlink/exports').glob('*.eqlink')):
    raw=path.read_bytes()
    if len(raw)>128000: continue
    data=json.loads(raw)
    if data.get('schemaVersion')==2 and data.get('grantId')==sys.argv[1]:
        print(json.dumps({'ok':True,'bundle':base64.b64encode(raw).decode()}))
        break
else:
    print(json.dumps({'ok':False}))
"""
    data = json.loads(kube.run("exec", "-n", namespace, "deployment/eqlink-server", "-c", "server", "--", "python3", "-c", query, grant))
    if not data.get("ok"):
        raise ValueError("The original export is not cached in this Pod; use --input with the administrator's existing private bundle file")
    return base64.b64decode(data["bundle"], validate=True)


def openssl_run(executable, *args):
    result = subprocess.run([executable, *map(str, args)], capture_output=True, timeout=15)
    if result.returncode:
        raise ValueError("CA/certificate validation failed; check the root, chain, hostname and validity period")
    return result.stdout


def add_hysteria_ca(kube, namespace, bundle_bytes, ca_path, expected_sha256, openssl=None):
    executable = openssl or shutil.which("openssl")
    if not executable:
        raise ValueError("OpenSSL is required on the management host; provide --openssl if it is outside PATH")
    if not re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256):
        raise ValueError("Expected CA SHA-256 must have exactly 64 hexadecimal digits")
    try:
        bundle = json.loads(bundle_bytes)
        network = bundle["network"]
        grant = bundle["grantId"]
        assert bundle["schemaVersion"] == 2 and re.fullmatch(r"[0-9a-f]{32}", grant)
        assert network.get("enableUdp", True)
        assert len(bundle_bytes) <= 128000
    except (KeyError, TypeError, ValueError, AssertionError):
        raise ValueError("A valid UDP-enabled universal v2 bundle is required") from None
    # Only public server state leaves the Pod; bearer tokens and device keys never
    # appear in exec arguments or its response.
    query = r"""import json,pathlib,re,sys
s=json.loads(pathlib.Path('/etc/eqlink/state.json').read_text())
text=pathlib.Path(s['settings']['hysteriaCertificate']).read_text()
chain=re.findall(r'-----BEGIN CERTIFICATE-----[\s\S]*?-----END CERTIFICATE-----', text)
print(json.dumps({'networkId':s['networkId'],'grantActive':any(g['id']==sys.argv[1] and g['active'] for g in s['grants']),'certificatePem':'\n'.join(chain)}))
"""
    metadata = json.loads(kube.run("exec", "-n", namespace, "deployment/eqlink-server", "-c", "server", "--", "python3", "-c", query, grant))
    if not metadata["grantActive"] or metadata["networkId"] != network["networkId"]:
        raise ValueError("The bundle does not belong to an active grant on this running network")
    ca_input = pathlib.Path(ca_path).read_bytes()
    if len(ca_input) > 65536 or b"PRIVATE KEY" in ca_input:
        raise ValueError("Provide a public CA certificate, not a private key")
    with tempfile.TemporaryDirectory(prefix="eqlink-public-ca-") as folder:
        root = pathlib.Path(folder)
        original, ca, chain = root / "ca.input", root / "ca.pem", root / "server-chain.pem"
        original.write_bytes(ca_input)
        encoding = "PEM" if b"-----BEGIN CERTIFICATE-----" in ca_input else "DER"
        if encoding == "PEM" and ca_input.count(b"-----BEGIN CERTIFICATE-----") != 1:
            raise ValueError("Provide one root CA; keep intermediate certificates in the server TLS chain")
        der = openssl_run(executable, "x509", "-inform", encoding, "-in", original, "-outform", "DER")
        if hashlib.sha256(der).hexdigest().lower() != expected_sha256.lower():
            raise ValueError("CA SHA-256 differs from the administrator-verified fingerprint")
        pem = openssl_run(executable, "x509", "-inform", encoding, "-in", original, "-outform", "PEM")
        ca.write_bytes(pem)
        text = openssl_run(executable, "x509", "-in", ca, "-text", "-noout")
        if b"CA:TRUE" not in text:
            raise ValueError("The supplied certificate is not a CA")
        openssl_run(executable, "verify", "-check_ss_sig", "-CAfile", ca, ca)
        chain.write_text(metadata["certificatePem"], encoding="utf-8")
        server_der = openssl_run(executable, "x509", "-in", chain, "-outform", "DER")
        pin = network["hysteriaCertificateSha256"].replace(":", "").lower()
        if hashlib.sha256(server_der).hexdigest() != pin:
            raise ValueError("The running Hysteria certificate differs from the bundle pin; retain the original bundle and inspect the certificate change")
        openssl_run(executable, "verify", "-purpose", "sslserver", "-verify_hostname", network["hysteriaServerName"], "-CAfile", ca, "-untrusted", chain, chain)
        network["hysteriaCaPem"] = pem.decode("ascii")
    return json.dumps(bundle, ensure_ascii=False, indent=2).encode("utf-8")
