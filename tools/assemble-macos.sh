#!/bin/bash
# Assemble a published official-core runtime plus the platform adapters.
# Usage: assemble-macos.sh <staging-dir> <code-signing-identity>
set -eu
work=$1
identity=$2
case "$work" in /tmp/.agents/*) ;; *) echo 'Staging must be under /tmp/.agents' >&2; exit 1;; esac
app="$work/Sefirah.app"
test ! -e "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$app/Contents/Library/LaunchServices"
tar -xzf "$work/mac-runtime.tar.gz" -C "$app/Contents/Resources"
runtime="$app/Contents/Resources/runtime"
clang -O2 -Wall -Wextra -Werror -arch arm64 "$work/mac-launcher.c" -o "$app/Contents/MacOS/Sefirah.Desktop"
clang -O2 -Wall -Wextra -Werror -arch arm64 -dynamiclib -framework AppKit -framework ServiceManagement "$work/mac-app-lifecycle.m" -o "$runtime/libSefirahMacLifecycle.dylib"
clang -O2 -Wall -Wextra -Werror -arch arm64 -framework AppKit "$work/mac-login-launcher.m" -Wl,-sectcreate,__TEXT,__info_plist,"$work/SefirahLoginLauncher-Info.plist" -o "$app/Contents/Library/LaunchServices/SefirahLoginLauncher"
cp "$work/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>Sefirah.Desktop</string>
<key>CFBundleIdentifier</key><string>com.castle.sefirah</string>
<key>CFBundleName</key><string>Sefirah</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>3.1.0</string>
<key>CFBundleVersion</key><string>66</string>
<key>CFBundleIconFile</key><string>AppIcon.icns</string>
<key>LSMinimumSystemVersion</key><string>11.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
chmod +x "$runtime/Sefirah.Desktop"
while IFS= read -r -d '' binary; do
    if file -b "$binary" | grep -q 'Mach-O'; then codesign --force --sign "$identity" "$binary"; fi
done < <(find "$runtime" -type f -print0)
codesign --force --sign "$identity" "$app/Contents/MacOS/Sefirah.Desktop"
codesign --force --sign "$identity" "$app/Contents/Library/LaunchServices/SefirahLoginLauncher"
codesign --force --sign "$identity" "$app"
codesign --verify --deep --strict "$app"
echo "Assembled and verified $app"
