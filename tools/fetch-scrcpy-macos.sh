#!/bin/bash
# Fetch the pinned official scrcpy macOS release into a staging directory for assemble-macos.sh.
# Usage: fetch-scrcpy-macos.sh <staging-dir>   (creates <staging-dir>/scrcpy)
set -eu
work=$1
case "$work" in /tmp/.agents/*) ;; *) echo 'Staging must be under /tmp/.agents' >&2; exit 1;; esac

version=5.0.1
sha256=33611e51977a8289e2e124b220f727895181ef2eb9060e0987a4dc2db9cf0d6b
name="scrcpy-macos-aarch64-v$version.tar.gz"
out="$work/scrcpy"
download="$work/scrcpy-download"

rm -rf "$out" "$download"
mkdir -p "$out" "$download"
curl -fsSL --retry 3 -o "$download/$name" "https://github.com/Genymobile/scrcpy/releases/download/v$version/$name"
echo "$sha256  $download/$name" | shasum -a 256 -c - >/dev/null || { echo "sha256 mismatch for $name" >&2; exit 1; }
tar -xzf "$download/$name" -C "$out" --strip-components=1
rm -rf "$download" "$out/scrcpy.1"

# The release is self-contained; refuse anything that would depend on Homebrew at runtime.
if otool -L "$out/scrcpy" "$out/adb" | grep -Eq '@rpath|@executable_path|@loader_path|/opt/|/usr/local/'; then
    echo 'scrcpy release has non-system dylib dependencies' >&2; exit 1
fi
chmod 755 "$out/scrcpy" "$out/adb"
printf '%s\n' "$version" > "$out/VERSION"
"$out/scrcpy" --version | head -1
echo "Fetched scrcpy $version into $out"
