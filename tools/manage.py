#!/usr/bin/env python3
"""Send a JSON request to the running same-user Sefirah desktop management pipe."""
import argparse
import getpass
import json
import os
import socket
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("request", help='JSON, for example {"method":"status"}')
    args = parser.parse_args()
    payload = json.dumps(json.loads(args.request), separators=(",", ":")).encode() + b"\n"
    if len(payload) >= 8192:
        parser.error("Request must be shorter than 8192 bytes")
    name = "sefirah-management-" + getpass.getuser()
    if os.name == "nt":
        with open("\\\\.\\pipe\\" + name, "r+b", buffering=0) as stream:
            stream.write(payload)
            result = bytearray()
            while not result.endswith(b"\n"):
                part = stream.read(1)
                if not part:
                    break
                result.extend(part)
    else:
        with socket.socket(socket.AF_UNIX) as connection:
            connection.settimeout(25)
            connection.connect(os.path.join(tempfile.gettempdir(), "CoreFxPipe_" + name))
            connection.sendall(payload)
            result = bytearray()
            while not result.endswith(b"\n"):
                part = connection.recv(65536)
                if not part:
                    break
                result.extend(part)
    response = json.loads(result)
    print(json.dumps(response, ensure_ascii=False, indent=2))
    if not response.get("ok"):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
