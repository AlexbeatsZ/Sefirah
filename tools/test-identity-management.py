#!/usr/bin/env python3
"""Exercise identity recovery against a running app using one disposable synthetic peer."""
import argparse
import base64
import hashlib
import json
import pathlib
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, help="Running app's control address")
    parser.add_argument("--port", type=int, default=5150)
    parser.add_argument("--android-serial")
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]

    def manage(method, argument=None, success=True):
        if args.android_serial:
            script = root.parent / "Sefirah-android/tools/manage.py"
            command = [sys.executable, str(script), "-s", args.android_serial, method]
            if argument is not None:
                command.append(argument if isinstance(argument, str) else json.dumps(argument))
        else:
            request = {"method": method}
            if isinstance(argument, str):
                request["deviceId"] = argument
            elif argument:
                request.update(argument)
            command = [sys.executable, str(root / "tools/manage.py"), json.dumps(request)]
        result = subprocess.run(command, capture_output=True, text=True, timeout=30)
        response = json.loads(result.stdout)
        assert response["ok"] == success, response
        return response.get("data")

    def wait_for(predicate):
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline:
            state = manage("status")
            if predicate(state):
                return state
            time.sleep(0.25)
        raise AssertionError("Timed out waiting for management state")

    initial = manage("status")
    device_id = str(uuid.uuid4())
    live = []
    temp_root = pathlib.Path(tempfile.gettempdir()) / ".agents"
    temp_root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="sefirah-identity-peer-", dir=temp_root) as temporary:
        work = pathlib.Path(temporary)

        def openssl(*arguments, data=None):
            return subprocess.run(["openssl", *map(str, arguments)], input=data, capture_output=True, check=True).stdout

        def public_key(der):
            return openssl("pkey", "-pubin", "-outform", "DER",
                data=openssl("x509", "-inform", "DER", "-pubkey", "-noout", data=der))

        def cert(name, key, serial):
            target = work / (name + ".pem")
            openssl("req", "-new", "-x509", "-key", key, "-out", target,
                "-subj", "/CN=" + name, "-days", "10", "-set_serial", serial)
            der = openssl("x509", "-in", target, "-outform", "DER")
            return target, key, der, public_key(der)

        for name in ("a", "b"):
            openssl("genpkey", "-algorithm", "EC", "-pkeyopt", "ec_paramgen_curve:P-256", "-out", work / (name + ".key"))
        first = cert("first", work / "a.key", "1")
        renewed = cert("renewed", work / "a.key", "2")
        replacement = cert("replacement", work / "b.key", "3")
        own_spki = public_key(base64.b64decode(initial["certificate"]))
        pin = lambda identity: hashlib.sha256(identity[3]).hexdigest().upper()
        code = lambda identity: hashlib.sha256(b"".join(sorted([own_spki, identity[3]], reverse=True))).hexdigest()[:8].upper()

        def connect(identity):
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
            context.load_cert_chain(identity[0], identity[1])
            connection = context.wrap_socket(socket.create_connection((args.host, args.port), timeout=5))
            connection.settimeout(5)
            assert connection.getpeercert(binary_form=True) == base64.b64decode(initial["certificate"])
            reader = connection.makefile("rb")
            live.append((connection, reader))
            connection.sendall((json.dumps({"type": "Authentication", "deviceId": device_id,
                "deviceName": "Sefirah identity regression", "model": "Regression",
                "publicKey": base64.b64encode(identity[3]).decode(), "protocolVersion": 1,
                "userInitiated": True}) + "\n").encode())
            return connection, reader

        def read_type(reader, kind):
            for _ in range(500):
                line = reader.readline()
                if not line:
                    return None
                message = json.loads(line)
                if message.get("type") == kind:
                    return message
            raise AssertionError("Expected protocol message missing")

        def paired(state):
            return next((peer for peer in state["paired"] if peer["deviceId"] == device_id), None)

        def close_all():
            while live:
                connection, reader = live.pop()
                try:
                    connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                reader.close()
                connection.close()

        checks = 0
        try:
            connection, reader = connect(first)
            assert read_type(reader, "Authentication")
            state = wait_for(lambda s: any(d["deviceId"] == device_id for d in s["discovered"]))
            candidate = next(d for d in state["discovered"] if d["deviceId"] == device_id)
            assert candidate["verificationCode"] == code(first)
            assert candidate["publicKeyFingerprint"] == pin(first)
            manage("pair", {"deviceId": device_id, "verificationCode": code(first)})
            assert read_type(reader, "PairMessage")["pair"]
            time.sleep(0.2)
            connection.sendall(b'{"type":"PairMessage","pair":true}\n')
            wait_for(lambda s: paired(s) and paired(s)["connected"])
            checks += 1
            close_all()
            wait_for(lambda s: paired(s) and not paired(s)["connected"])

            _, reader = connect(renewed)
            assert read_type(reader, "Authentication")
            wait_for(lambda s: paired(s) and paired(s)["connected"])
            checks += 1
            close_all()
            wait_for(lambda s: paired(s) and not paired(s)["connected"])

            _, reader = connect(replacement)
            assert read_type(reader, "Authentication") is None, "Different key was accepted"
            assert paired(manage("status"))["publicKeyFingerprint"] == pin(first)
            checks += 1
            close_all()
            request = {"deviceId": device_id, "certificate": base64.b64encode(replacement[2]).decode(),
                "expectedFingerprint": pin(first), "publicKeyFingerprint": pin(replacement),
                "verificationCode": code(replacement)}
            for field in ("expectedFingerprint", "publicKeyFingerprint", "verificationCode"):
                manage("rebind", dict(request, **{field: "INVALID"}), success=False)
                assert paired(manage("status"))["publicKeyFingerprint"] == pin(first)
                checks += 1
            manage("rebind", request)
            assert paired(manage("status"))["publicKeyFingerprint"] == pin(replacement)
            _, reader = connect(replacement)
            assert read_type(reader, "Authentication")
            wait_for(lambda s: paired(s) and paired(s)["connected"])
            checks += 1
            close_all()
            wait_for(lambda s: paired(s) and not paired(s)["connected"])
            _, reader = connect(first)
            assert read_type(reader, "Authentication") is None, "Old key survived explicit rebind"
            assert paired(manage("status"))["publicKeyFingerprint"] == pin(replacement)
            close_all()
            _, reader = connect(replacement)
            assert read_type(reader, "Authentication")
            wait_for(lambda s: paired(s) and paired(s)["connected"])
            checks += 1
            manage("unpair", device_id)
            assert paired(manage("status")) is None
            close_all()
            _, reader = connect(replacement)
            assert read_type(reader, "Authentication")
            assert paired(manage("status")) is None
            checks += 1
        finally:
            close_all()
            if paired(manage("status")):
                manage("unpair", device_id)
        final = manage("status")
        baseline = {p["deviceId"]: p["publicKeyFingerprint"] for p in initial["paired"]}
        assert {p["deviceId"]: p["publicKeyFingerprint"] for p in final["paired"]} == baseline
        assert final["deviceId"] == initial["deviceId"]
        assert final.get("storageLocation") == initial.get("storageLocation")
        print(f"PASS {checks} live identity/management checks; original peers and local identity preserved")


if __name__ == "__main__":
    main()
