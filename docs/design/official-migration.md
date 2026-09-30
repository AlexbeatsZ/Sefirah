# Official source migration and macOS adaptation

## Scope and baseline

Windows runs the upstream 3.1.0 Microsoft Store-signed package (`shrimqy.Seki-PhoneLink`). Android runs the upstream 3.1.0 APK payload signed with the existing installation key. This keeps the installed Android UID, data, permission grants and certificate identity; a differently signed upstream APK cannot replace it directly.

macOS has no upstream release asset. The local arm64 build links the official desktop source at `bfea958ae3ed4f5d1b6d39131a11b8ff3237e9b8`. Its additions are the Uno macOS host, AppKit window/status-item/login lifecycle, notifications, battery, system volume, SFTP URI browsing and macOS power commands. Fork device groups, control API and Bluetooth handoff are outside this adaptation.

The dedicated `src/Sefirah.Desktop` project retains the executable name `Sefirah.Desktop`. Uno derives the application data location from this name. The bundle ID `com.castle.sefirah`, existing local signing identity and `~/Library/Application Support/Sefirah.Desktop/LocalState` remain stable. A new assembly name can silently create a different data directory and lose the visible pairing list.

## Preserving identity

Before replacing a program, stop its process and preserve its database, PFX, device settings and general settings in the task staging directory. The selected official desktop schema is 5; the Android schema is 2. Do not copy a database across incompatible schema versions: upstream's unsupported-version fallback can recreate it.

Windows package identities differ, so copy the old LocalState to the new package's LocalState before activation. Start the MSIX through `shell:AppsFolder/<family>!App`; running `Sefirah.exe` directly lacks package identity and fails at `Package.Current`. Remote AppX deployment must run in the logged-in user's interactive scheduled task, not the SSH service context.

For Android, verify signing compatibility before `adb install -r -d`. The official version code is 35 while the old fork code was 52; `-d` is needed for this intentional downgrade. Do not uninstall or clear application data. Check that the original first-install timestamp remains and that trusted peers authenticate afterward. The release APK is not debuggable, so post-update `run-as` is unavailable.

Compare local IDs, schema, peer certificate bytes and the private PFX before/after. Do not compare a whole live database hash: address discovery and ordinary message delivery change it. A TCP connection alone does not prove trusted authentication. Use explicit `Paired device ... verified` logs or the Android connected UI with current battery/audio messages.

## Legacy actions and missing Android icons

The fork stored polymorphic `$type: Process` actions with top-level `Path` and `Arguments`. Official 3.1.0 uses `ActionId`, `Icon` and a `Settings` object. Copying old JSON without conversion results in generic Apps icons and unusable command configuration.

`tools/migrate-legacy-actions.py` recognizes exact built-in Windows/Linux power commands and translates them to official Power actions. It preserves IDs, names and explicit confirmation preferences. Unknown process commands become Run actions with their existing arguments and execution settings. Unknown legacy types abort before writing. Existing official actions are unchanged. The converter backs up the original file and atomically replaces it, while Sefirah is stopped. Reconnect Android peers to receive the refreshed action list.

Verify with `uv run --no-project tools/test_migrate_legacy_actions.py`. Never test migration by actually invoking shutdown, restart, logoff or lock actions on a live device.

## Building and packaging macOS

Publish with the existing project SDK:

```powershell
dotnet publish src/Sefirah.Desktop/Sefirah.Desktop.csproj -c Release -r osx-arm64 --self-contained -p:PublishDir=C:/Users/Meta/Project/Workspaces/Sefirah/Sefirah/artifacts/osx-arm64/runtime/
```

The Uno macOS package is pinned to `6.8.0-dev.46`, matching the currently resolved Uno runtime. Copy the published `runtime` directory as `mac-runtime.tar.gz` plus the three native tool sources, AppIcon and login-launcher plist into `/tmp/.agents/<task>/`. `tools/assemble-macos.sh <staging-dir> <existing-signing-identity>` compiles the bridges and assembles/signs the bundle. Compile native code with `-Wall -Wextra -Werror`; validate with `codesign --verify --deep --strict`.

Windows tar archives do not retain the published Mach-O executable bit. Always `chmod +x` the runtime `Sefirah.Desktop` after the final archive extraction and before signing. Re-extracting a publish archive over a staged app removes that bit again; the native launcher then exits with `Permission denied` even though the signature verifies.

SSH code signing may fail with `errSecInternalComponent` because its session cannot access the login keychain identity. A temporary LaunchAgent in the existing GUI user's domain can sign with the same identity without changing keychain permissions. Remove temporary sign/debug agents and Windows migration tasks when finished; keep only the established login-startup agent.

## macOS limits

The upstream Linux media-session implementation remains a no-op on macOS. The adaptation exposes one system output for volume/mute, sends its state on connection and after remote changes, and does not enumerate CoreAudio outputs or continuously track external volume changes. SFTP browsing launches a configured external handler; it does not mount a filesystem. Hibernate has no supported macOS equivalent and logs that limitation; no global power configuration is changed. Power commands may require the existing Automation/Accessibility grants and are not acceptance-tested by disrupting a logged-in session.

Closing the window hides it while networking remains active. Status-item clicks and application reopen display the existing window. Login startup uses the established `SefirahLoginLauncher` and `--login-startup` argument.
