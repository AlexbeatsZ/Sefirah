#!/bin/bash
# Fetch the app-local SFTP/WebDAV bridge. No global installation is required.
set -eu
work=$1
case "$work" in /tmp/.agents/*) ;; *) echo 'Staging must be under /tmp/.agents' >&2; exit 1;; esac
version=1.75.2
name="rclone-v$version-osx-arm64"
sha256=610ab7d48615135dfc60b763858b11a426a0d3d1160c1bb76fb0ae6011b02edd
download=$(mktemp -d "$work/rclone-download.XXXXXX")
trap 'rm -rf "$download"' EXIT
curl -fsSL --retry 3 -o "$download/release.zip" "https://downloads.rclone.org/v$version/$name.zip"
echo "$sha256  $download/release.zip" | shasum -a 256 -c - >/dev/null
unzip -q "$download/release.zip" -d "$download"
mkdir -p "$work/rclone"
cp "$download/$name/rclone" "$download/$name/README.txt" "$work/rclone/"
chmod 755 "$work/rclone/rclone"
printf '%s\n' "$version" > "$work/rclone/VERSION"
# This is a build-time dependency check, not a runtime Homebrew dependency.
if otool -L "$work/rclone/rclone" | grep -Eq '@rpath|@executable_path|@loader_path|/opt/|/usr/local/'; then
    echo 'rclone has non-system dylib dependencies' >&2; exit 1
fi
"$work/rclone/rclone" version | head -1
