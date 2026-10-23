# macOS phone storage in Finder

## Scope and behavior

Build 74 adds connected phone storage to Finder. Each Android `SftpServerInfo.Paths`
entry becomes a separate named network volume. The per-device storage setting
controls automatic mounting and takes effect immediately. Browse files opens the
mounted folder; its explicit external-SFTP action still opens/copies an SFTP URI.
No protocol, Android package, pairing certificate or LocalState schema changes.

The default mount root matches Windows: `~/RemoteDevices/<device name>`.
A single share mounts directly at the device folder; multiple shares use
`~/RemoteDevices/<device name>/<share name>`. Names are sanitized; only name
collisions or occupied local folders add a stable device suffix. Existing files
and symlinks are never covered by a mount. The General settings storage-location
picker uses the existing `RemoteStoragePath` setting and moves active Mac volumes
immediately after a change.
Finder may group these network volumes under the loopback server `127.0.0.1` in
its sidebar; volume/window names identify the phone and its storage.

## Architecture

`MacSftpFeature` manages sessions, storage preferences, duplicate messages, eject,
disconnect/reconnect and cleanup. `MacStorageMount` starts a bundled rclone SFTP
backend as a private loopback WebDAV server. A bundled Go standard-library reverse proxy validates complete GET responses before passing them to macOS. `/sbin/mount_webdav` mounts it using
the macOS filesystem implementation, so Finder and ordinary application file
dialogs can read, write, rename, create and delete remote files.

This uses no FUSE driver, package-manager install, Apple developer entitlement,
new login agent or system configuration. rclone 1.75.2 arm64 is fetched from the
official distribution by `tools/fetch-rclone-macos.sh`, with a pinned archive
digest and a check against non-system dylib dependencies. Its README includes
the MIT license. `assemble-macos.sh` signs rclone, `SefirahStorageWorker` and `SefirahStorageProxy` with
the existing app identity in `Contents/Resources/rclone`.

The bridge binds **only** `127.0.0.1` on an OS-assigned port. A fresh 256-bit
capability path is required for access; the root without it returns 404. Remote
passwords enter `rclone obscure` over stdin and the bridge through environment
variables; there is no password on argv, config file, app log or normal browse
clipboard. The explicit SFTP URI action retains upstream's credential-bearing
URI behavior. Android SFTP credentials arrive over the existing paired channel;
SFTP host-key handling follows the upstream desktop's password-based behavior.
No user's SSH config, keys or known_hosts file is modified. Shell execution and
remote checksum commands are disabled because Android offers only SFTP.

## Lifecycle and write semantics

- Each volume has one native worker, rclone and a Go download-validation process. The worker watches both
  the Sefirah PID and rclone with kqueue. The validator independently watches its Sefirah parent PID. Managed disposal, parent exit/crash,
  SIGTERM and bridge failure all unmount before stopping the bridge.
- Worker cleanup verifies the exact filesystem type and mountpoint before
  unmounting. It tries a normal unmount first, then forces disconnection if busy.
  The server has no deferred VFS disk write-back cache. Reads are staged in private `/tmp/.agents/sefirah-storage-read-*` temporary files until the entire HTTP response is validated; files are unlinked immediately on open, so even SIGKILL removes them. Reading a file or range therefore needs temporary disk space and waits for that response to download completely. A lost connection can
  fail an operation; it does not promise offline editing or queued uploads.
- Directory listing cache is two seconds. macOS Finder has its own network
  filesystem cache; an external phone-side change may require Finder refresh.
- `-S` prevents authentication and unresponsive-server dialogs and makes macOS
  disconnect an unresponsive local server. SFTP operations, startup and teardown
  are bounded. A disconnect or disabling storage cancels pending startup.
- Duplicate connection information reuses live mounts. Finder eject is detected
  on the next Browse files action and the volume is recreated. New credentials,
  host, name or shared paths replace the session.
- A failed share rolls back the device's other mounts. Missing dependencies or
  inaccessible storage fall back to the original external SFTP browsing action.
- Cleanup deletes **only empty local directories**, never recursively. Do not
  replace this with recursive deletion: an unsuccessful unmount could otherwise
  delete the phone's files.

## Failure cases established during implementation

The WebDAV/VFS root can report an empty directory when the real SFTP root does
not exist, even for PROPFIND Depth 1. Run a direct `rclone lsf` preflight against
the advertised share before mounting; a listening server or successful PROPFIND
alone does not prove remote storage accessibility.

Uno `Launcher.LaunchFolderPathAsync` is not implemented on this macOS target.
Use `/usr/bin/open` with an argument list to open the actual mounted path.

Finder MOVE/COPY may use an absolute Destination URL. Rewrite the request Host
and Destination/If authorities consistently to the backend; mismatched
authorities cause Finder folder rename error -43 even when basic file moves pass.

The native WebDAV filesystem can interpret a truncated HTTP 200 as a successful
empty file read when the SFTP server disappears. The validator buffers successful
GET responses completely before sending headers, returning an HTTP error on
truncation. Force upstream `Accept-Encoding: identity` and disable automatic gzip:
a failed remote read can otherwise become a valid empty gzip stream, hiding its
expected length. Error responses for failed uploads are preserved.

Keep protocol forwarding in Go's standard `httputil.ReverseProxy`. A tested
HttpListener forwarding implementation caused native LOCK/UNLOCK failures
(Resource busy / Operation not permitted), while direct rclone and the Go
transport passed the same native filesystem operations.

## Build and acceptance

```sh
tools/fetch-rclone-macos.sh /tmp/.agents/<staging>
dotnet build tools/tests/MacStorage/MacStorage.csproj -c Release
uv run --no-project --with paramiko tools/test_macos_storage.py /tmp/.agents/<staging>
```

Build `tools/mac-storage-worker.c` into `<staging>/rclone/SefirahStorageWorker`
with `clang -O2 -Wall -Wextra -Werror -arch arm64` before the test. Build
`tools/mac-storage-proxy.go` into `<staging>/rclone/SefirahStorageProxy` using the
existing Go toolchain, `GOOS=darwin GOARCH=arm64 CGO_ENABLED=0` and a staging-local
GOCACHE. The regular
bundle assembly builds it automatically. Tests link the production mount and
feature classes, with a minimal Uno host substitute, and exercise real macOS
mounts against a temporary SFTP server. `--preview` keeps a fixture volume open
in Finder for UI checks; Ctrl-C cleans it up.

Verified on macOS 26.6.2: native mounts, absolute-destination folder moves, UTF-8/special filenames, reads, overwrite,
random writes and flush, create/rename/delete, multi-volume isolation, eject and
remount, invalid password/root cleanup, duplicate-session reuse, preference
toggle, disconnect/reconnect, removal, partial-failure rollback and parent
SIGKILL cleanup. Actual Finder create and rename reached the SFTP fixture.
Multi-megabyte binary contents and range offsets are checked too. Closing the
fixture SFTP server verifies failed downloads and uploads return errors, mounted
reads fail (rather than becoming empty files), and cleanup still works.
Build 74 is installed with the original signing identity, local ID, PFX, four peer
certificates and saved general/device settings preserved. Redmi K70 authenticated
and mounted its actual `/storage/emulated/0` at `~/RemoteDevices/Redmi K70`.
The 43 fixture checks also cover friendly Windows-style paths, immediate location
changes, equal device names and occupied local directories. Actual Android writes
are still under verification: the latest attempt failed while its SFTP port
accepted TCP but did not return an SSH banner. Listing a previously cached
directory is insufficient to claim write or device network-loss acceptance.
Automated fixtures may leave Finder volume icons cached after kernel unmount;
verify both the real mount table and Finder, and refresh Finder only with authority.

Primary references: [Apple mount_webdav source/manual](https://github.com/apple-oss-distributions/webdavfs/blob/main/mount.tproj/mount_webdav.8),
[rclone WebDAV server](https://rclone.org/commands/rclone_serve_webdav/),
[rclone SFTP backend](https://rclone.org/sftp/).
