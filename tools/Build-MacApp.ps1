param(
    [ValidateSet('osx-arm64', 'osx-x64')]
    [string]$RuntimeIdentifier = 'osx-arm64',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$DeployToMac,
    [string]$MacHost = '100.64.2.94',
    [string]$MacUser = 'meta',
    [string]$MacDestination = '/Users/meta/Applications',
    [string]$CodeSignIdentity = 'Local Development Code Signing'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Sefirah.Desktop\Sefirah.Desktop.csproj'
$artifactDirectory = Join-Path $root "artifacts\$RuntimeIdentifier"
$appBundle = Join-Path $artifactDirectory 'Sefirah.app'
$contentsDir = Join-Path $appBundle 'Contents'
$macOsDir = Join-Path $contentsDir 'MacOS'
$resourcesDir = Join-Path $contentsDir 'Resources'
$macIcon = Join-Path $root 'packaging\macos\AppIcon.icns'

if (-not (Test-Path -LiteralPath $macIcon)) {
    throw "macOS application icon not found at $macIcon"
}

Write-Host "Publishing Sefirah.Desktop for $RuntimeIdentifier ($Configuration)..." -ForegroundColor Cyan

$publishArgs = @(
    'publish',
    $project,
    '-f', 'net10.0-desktop',
    '-r', $RuntimeIdentifier,
    '-c', $Configuration,
    '--self-contained'
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishDir = Join-Path $root "src\Sefirah.Desktop\bin\$Configuration\net10.0-desktop\$RuntimeIdentifier\publish"
if (-not (Test-Path -LiteralPath $publishDir)) {
    throw "Publish directory not found at $publishDir"
}

Write-Host "Assembling macOS application bundle: $appBundle..." -ForegroundColor Cyan

if (Test-Path -LiteralPath $appBundle) {
    $resolvedBundle = [IO.Path]::GetFullPath($appBundle)
    if (-not $resolvedBundle.StartsWith([IO.Path]::GetFullPath($artifactDirectory) + [IO.Path]::DirectorySeparatorChar)) {
        throw 'App bundle path escaped the artifact directory.'
    }
    Remove-Item -LiteralPath $appBundle -Recurse -Force
}

New-Item -ItemType Directory -Path $macOsDir, $resourcesDir -Force | Out-Null

Copy-Item -Path "$publishDir\*" -Destination $macOsDir -Recurse -Force
Copy-Item -LiteralPath $macIcon -Destination (Join-Path $resourcesDir 'AppIcon.icns') -Force

$infoPlist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>Sefirah.Desktop</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.castle.sefirah</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>Sefirah</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>3.1.0</string>
    <key>CFBundleVersion</key>
    <string>64</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSBluetoothAlwaysUsageDescription</key>
    <string>Sefirah uses Bluetooth to show paired devices and hand off your selected headset between paired devices.</string>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
</dict>
</plist>
"@

$infoPlistPath = Join-Path $contentsDir 'Info.plist'
[System.IO.File]::WriteAllText($infoPlistPath, $infoPlist, [System.Text.Encoding]::UTF8)

$pkgInfoPath = Join-Path $contentsDir 'PkgInfo'
[System.IO.File]::WriteAllText($pkgInfoPath, "APPL????", [System.Text.Encoding]::ASCII)

Write-Host "Bundle assembled successfully at $appBundle" -ForegroundColor Green

if ($DeployToMac) {
    if ($MacUser -notmatch '^[a-zA-Z0-9._-]+$' -or $MacDestination -ne "/Users/$MacUser/Applications") {
        throw 'Deployment currently supports the target user Applications directory only.'
    }
    if ($CodeSignIdentity -notmatch '^[a-zA-Z0-9 ._()-]+$') {
        throw 'The code-signing identity contains unsupported characters.'
    }
    $remoteWork = '/tmp/.agents/sefirah-deploy-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    $archive = Join-Path $artifactDirectory 'Sefirah-mac.tar.gz'
    & tar -czf $archive -C $artifactDirectory Sefirah.app
    if ($LASTEXITCODE -ne 0) { throw 'App archive failed.' }
    & ssh -o BatchMode=yes "$MacUser@$MacHost" "mkdir -p '$remoteWork'"
    if ($LASTEXITCODE -ne 0) { throw 'Remote staging failed.' }
    & scp $archive "${MacUser}@${MacHost}:${remoteWork}/app.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw 'App upload failed.' }
    & scp (Join-Path $PSScriptRoot 'mac-launcher.c') "${MacUser}@${MacHost}:${remoteWork}/launcher.c"
    if ($LASTEXITCODE -ne 0) { throw 'Launcher upload failed.' }
    & scp (Join-Path $PSScriptRoot 'mac-bluetooth-helper.m') "${MacUser}@${MacHost}:${remoteWork}/bluetooth-helper.m"
    if ($LASTEXITCODE -ne 0) { throw 'Bluetooth helper upload failed.' }
    $deployScript = @'
set -eu
work='__WORK__'
destination='__DEST__'
signing_identity='__SIGNING_IDENTITY__'
mkdir -p "$work/stage" "$destination"
security find-identity -v -p codesigning | grep -F -- "\"$signing_identity\"" >/dev/null
tar -xzf "$work/app.tar.gz" -C "$work/stage"
test -f "$work/stage/Sefirah.app/Contents/MacOS/Sefirah.Desktop"
mv "$work/stage/Sefirah.app/Contents/MacOS" "$work/stage/Sefirah.app/Contents/Resources/runtime"
mkdir "$work/stage/Sefirah.app/Contents/MacOS"
clang -O2 -Wall -Wextra -Werror -arch __ARCH__ "$work/launcher.c" -o "$work/stage/Sefirah.app/Contents/MacOS/Sefirah.Desktop"
clang -O2 -Wall -Wextra -Werror -fobjc-arc -arch __ARCH__ -framework Foundation -framework IOBluetooth "$work/bluetooth-helper.m" -o "$work/stage/Sefirah.app/Contents/Resources/runtime/sefirah-bluetooth"
chmod +x "$work/stage/Sefirah.app/Contents/Resources/runtime/Sefirah.Desktop"
chmod +x "$work/stage/Sefirah.app/Contents/Resources/runtime/sefirah-bluetooth"
while IFS= read -r -d '' binary; do
    if file -b "$binary" | grep -q 'Mach-O'; then
        codesign --force --timestamp=none --sign "$signing_identity" "$binary"
    fi
done < <(find "$work/stage/Sefirah.app/Contents/Resources/runtime" -type f -print0)
codesign --force --timestamp=none --sign "$signing_identity" "$work/stage/Sefirah.app"
codesign --verify --deep --strict "$work/stage/Sefirah.app"
for pid in $(pgrep -f "^$destination/Sefirah.app/Contents/(MacOS|Resources/runtime)/Sefirah.Desktop$" || true); do
    kill -TERM "$pid"
done
for attempt in 1 2 3 4 5; do
    if ! pgrep -f "^$destination/Sefirah.app/Contents/(MacOS|Resources/runtime)/Sefirah.Desktop$" >/dev/null; then break; fi
    sleep 1
done
if pgrep -f "^$destination/Sefirah.app/Contents/(MacOS|Resources/runtime)/Sefirah.Desktop$" >/dev/null; then
    for pid in $(pgrep -f "^$destination/Sefirah.app/Contents/(MacOS|Resources/runtime)/Sefirah.Desktop$"); do
        kill -KILL "$pid"
    done
    sleep 1
fi
if pgrep -f "^$destination/Sefirah.app/Contents/(MacOS|Resources/runtime)/Sefirah.Desktop$" >/dev/null; then
    echo 'Existing Sefirah process could not be stopped; installation cancelled.' >&2
    exit 1
fi
state="$HOME/Library/Application Support/Sefirah.Desktop/LocalState"
if [ -d "$state" ]; then cp -R "$state" "$work/LocalState-backup"; fi
if [ -d "$destination/Sefirah.app" ]; then mv "$destination/Sefirah.app" "$work/Sefirah.previous.app"; fi
if ! mv "$work/stage/Sefirah.app" "$destination/Sefirah.app"; then
    if [ -d "$work/Sefirah.previous.app" ]; then mv "$work/Sefirah.previous.app" "$destination/Sefirah.app"; fi
    exit 1
fi
open "$destination/Sefirah.app"
echo "Installed. Rollback app and state: $work"
'@
    $macArchitecture = if ($RuntimeIdentifier -eq 'osx-arm64') { 'arm64' } else { 'x86_64' }
    $deployScript = $deployScript.Replace('__WORK__', $remoteWork).Replace('__DEST__', $MacDestination).Replace('__ARCH__', $macArchitecture).Replace('__SIGNING_IDENTITY__', $CodeSignIdentity).Replace("`r", '')
    $deployScript | & ssh -o BatchMode=yes "$MacUser@$MacHost" 'bash -s'
    if ($LASTEXITCODE -ne 0) { throw 'Remote deployment failed; inspect the retained staging/backup directory.' }
    Write-Host 'App installed and launch requested. Verify runtime connectivity separately.' -ForegroundColor Green
}
