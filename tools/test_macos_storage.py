#!/usr/bin/env python3
"""Real native mount -> WebDAV -> SFTP integration; no privileged/system changes.

uv run --no-project --with paramiko tools/test_macos_storage.py <staging-dir>
Build tools/tests/MacStorage first. All remote files are isolated fixtures.
"""
import contextlib
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time

import paramiko


class Server(paramiko.ServerInterface):
    def check_auth_password(self, username, password):
        return paramiko.AUTH_SUCCESSFUL if username == "fixture" and secrets.compare_digest(password, secret) else paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password"

    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED


class Files(paramiko.SFTPServerInterface):
    def local(self, path):
        result = (root / path.lstrip("/")).resolve()
        if not result.is_relative_to(root):
            raise OSError(13, "outside fixture")
        return result

    def list_folder(self, path):
        try:
            items = []
            for child in self.local(path).iterdir():
                attr = paramiko.SFTPAttributes.from_stat(child.stat())
                attr.filename = child.name
                items.append(attr)
            return items
        except OSError as exc:
            return paramiko.SFTPServer.convert_errno(exc.errno)

    def stat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(self.local(path).stat())
        except OSError as exc:
            return paramiko.SFTPServer.convert_errno(exc.errno)

    lstat = stat

    def open(self, path, flags, attr):
        try:
            fd = os.open(self.local(path), flags, 0o600)
            mode = "r+b" if flags & os.O_RDWR else "wb" if flags & os.O_WRONLY else "rb"
            stream = os.fdopen(fd, mode, buffering=0)
            handle = paramiko.SFTPHandle(flags)
            handle.readfile = stream
            handle.writefile = stream
            handle.stat = lambda: paramiko.SFTPAttributes.from_stat(os.fstat(stream.fileno()))
            return handle
        except OSError as exc:
            return paramiko.SFTPServer.convert_errno(exc.errno)

    def mutate(self, action):
        try:
            action()
            return paramiko.SFTP_OK
        except OSError as exc:
            return paramiko.SFTPServer.convert_errno(exc.errno)

    def remove(self, path):
        return self.mutate(lambda: self.local(path).unlink())

    def mkdir(self, path, attr):
        return self.mutate(lambda: self.local(path).mkdir())

    def rmdir(self, path):
        return self.mutate(lambda: self.local(path).rmdir())

    def rename(self, oldpath, newpath):
        return self.mutate(lambda: self.local(oldpath).rename(self.local(newpath)))

    posix_rename = rename

    def chattr(self, path, attr):
        return self.mutate(lambda: paramiko.SFTPServer.set_file_attr(str(self.local(path)), attr))


with tempfile.TemporaryDirectory(prefix="fixture-", dir=Path(sys.argv[1]).resolve()) as temporary:
    root = Path(temporary).resolve()
    (root / "share").mkdir()
    (root / "second").mkdir()
    (root / "second" / "second.txt").write_text("second volume")
    (root / "share" / "原始 [1].txt").write_text("phone fixture\n")
    (root / "share" / "network-loss.txt").write_text("network loss fixture")
    (root / "outside.txt").write_text("untouched")
    secret = secrets.token_hex(20)
    key = paramiko.RSAKey.generate(2048)
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    transports = []

    def accept():
        while True:
            try:
                conn, _ = listener.accept()
            except OSError:
                return
            if (root / "drop-sftp").exists():
                conn.close()
                return
            transport = paramiko.Transport(conn)
            transports.append(transport)
            transport.add_server_key(key)
            transport.set_subsystem_handler("sftp", paramiko.SFTPServer, Files)
            threading.Thread(target=lambda t=transport: t.start_server(server=Server()), daemon=True).start()

    threading.Thread(target=accept, daemon=True).start()

    def drop_remote():
        while not (root / "drop-sftp").exists() and root.exists():
            time.sleep(0.1)
        if not root.exists():
            return
        with contextlib.suppress(OSError):
            listener.shutdown(socket.SHUT_RDWR)
        listener.close()
        for transport in transports:
            with contextlib.suppress(Exception):
                transport.close()
        (root / "sftp-stopped").touch()

    threading.Thread(target=drop_remote, daemon=True).start()
    env = os.environ | {
        "SEFIRAH_STORAGE_STAGE": os.environ.get("SEFIRAH_STORAGE_BINARIES", str(Path(sys.argv[1]).resolve())),
        "SEFIRAH_STORAGE_FIXTURE": str(root),
        "SEFIRAH_STORAGE_PORT": str(listener.getsockname()[1]),
        "SEFIRAH_STORAGE_PASSWORD": secret,
    }
    project = Path(__file__).parent / "tests/MacStorage/bin/Release/net10.0/MacStorage.dll"
    command = ["/usr/local/share/dotnet/dotnet", str(project.resolve())]
    try:
        if "--network-only" in sys.argv:
            subprocess.run(command + ["network-loss"], env=env, check=True, timeout=60)
            sys.exit(0)
        if "--preview" in sys.argv:
            preview = subprocess.Popen(command + ["hold", "open"], env=env)
            print(f"Finder preview: {root / 'mounted'}", flush=True)
            try:
                preview.wait()
            finally:
                if preview.poll() is None:
                    preview.terminate()
                    preview.wait(timeout=10)
            sys.exit(0)
        subprocess.run(command, env=env, check=True, timeout=150)
        # SIGKILL bypasses managed Dispose/ProcessExit: the native owner watcher
        # must still remove its mount and child bridge.
        owner = subprocess.Popen(command + ["hold"], env=env, stdout=subprocess.PIPE, text=True)
        try:
            assert owner.stdout.readline().strip() == "MOUNTED"
            table = subprocess.check_output(["ps", "-axo", "pid=,ppid="], text=True)
            family = {owner.pid}
            relations = [tuple(map(int, line.split())) for line in table.splitlines() if line.strip()]
            for _ in range(3):
                family.update(pid for pid, parent in relations if parent in family)
            owner.kill()
            owner.wait(timeout=10)
            for _ in range(100):
                if not os.path.ismount(root / "mounted"):
                    break
                time.sleep(0.1)
            assert not os.path.ismount(root / "mounted"), "crashed owner left a mounted volume"
            print("PASS SIGKILL owner unmounts native volume", flush=True)

            def alive(pid):
                try:
                    os.kill(pid, 0)
                    return True
                except ProcessLookupError:
                    return False

            for _ in range(80):
                if not any(alive(pid) for pid in family - {owner.pid}):
                    break
                time.sleep(0.1)
            assert not any(alive(pid) for pid in family - {owner.pid}), "crashed owner left storage helper processes"
            print("PASS SIGKILL owner stops bridge and download validator", flush=True)
        finally:
            if owner.poll() is None:
                owner.kill()
        subprocess.run(command + ["network-loss"], env=env, check=True, timeout=60)
    finally:
        listener.close()
        for transport in transports:
            with contextlib.suppress(Exception):
                transport.close()
