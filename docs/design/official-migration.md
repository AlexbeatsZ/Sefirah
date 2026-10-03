# Official source migration and macOS adaptation

## Scope and baseline

Windows runs the upstream 3.1.0 Microsoft Store-signed package (`shrimqy.Seki-PhoneLink`). Android runs the upstream 3.1.0 APK payload signed with the existing installation key. This keeps the installed Android UID, data, permission grants and certificate identity; a differently signed upstream APK cannot replace it directly.

macOS has no upstream release asset. The local arm64 build links the official desktop source at `bfea958ae3ed4f5d1b6d39131a11b8ff3237e9b8`. Its additions are the Uno macOS host, AppKit window/status-item/login lifecycle, notifications, battery, system volume, SFTP URI browsing and macOS power commands. Fork device groups, control API and Bluetooth handoff are outside this adaptation.

The dedicated `src/Sefirah.Desktop` project retains the executable name `Sefirah.Desktop`. Uno derives the application data location from this name. The bundle ID `com.castle.sefirah`, existing local signing identity and `~/Library/Application Support/Sefirah.Desktop/LocalState` remain stable. A new assembly name can silently create a different data directory and lose the visible pairing list.

The native `Contents/MacOS/Sefirah.Desktop` executable hosts the self-contained runtime through `hostfxr_main_startupinfo`, keeping the managed assembly and runtime in Resources. Do not replace this with `execv` into Resources: that makes `NSBundle.mainBundle` lose the application identity, so native Notification Center registration fails even though pairing and the window still work. Keep the host process, working directory, managed assembly name and launch arguments stable.

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

The Uno macOS package is pinned to `6.8.0-dev.46`, matching the currently resolved Uno runtime. Copy the published `runtime` directory as `mac-runtime.tar.gz` plus `mac-launcher.c`, `mac-login-launcher.m`, `mac-app-lifecycle.m`, `mac-audio.m`, `mac-notifications.m`, AppIcon and login-launcher plist into `/tmp/.agents/<task>/`. `tools/assemble-macos.sh <staging-dir> <existing-signing-identity>` compiles the bridges and assembles/signs the bundle. Compile native code with `-Wall -Wextra -Werror`; validate with `codesign --verify --deep --strict`.

The desktop project overrides SSH.NET to 2026.0.0 and its required BouncyCastle.Cryptography to 2.7.0. This removes the 2025.1.0 NU1903 restore warnings without changing the official Windows project's central package versions. The desktop adaptation does not use SCP; SFTP browsing still invokes an external handler.

Windows tar archives do not retain the published Mach-O executable bit. Always `chmod +x` the runtime `Sefirah.Desktop` after the final archive extraction and before signing. Re-extracting a publish archive over a staged app removes that bit again; the native launcher then exits with `Permission denied` even though the signature verifies.

SSH code signing may fail with `errSecInternalComponent` because its session cannot access the login keychain identity. A temporary LaunchAgent in the existing GUI user's domain can sign with the same identity without changing keychain permissions. Remove temporary sign/debug agents and Windows migration tasks when finished; keep only the established login-startup agent.

## macOS limits

The upstream Linux media-session implementation remains a no-op on macOS. The adaptation exposes one system output for volume/mute. CoreAudio listeners follow the default output and its volume/mute properties; a 100 ms debounce reads the system state and sends Active updates to connected AudioSync peers. Registration changes and callbacks are serialized on the main queue, while AppleScript reads/actions are bounded to five seconds. Initial connection still sends New; duplicate state broadcasts are suppressed. The adaptation does not enumerate separate output devices. SFTP browsing launches a configured external handler; it does not mount a filesystem. Hibernate has no supported macOS equivalent and logs that limitation; no global power configuration is changed. Power commands may require the existing Automation/Accessibility grants and are not acceptance-tested by disrupting a logged-in session.

Notifications use UNUserNotificationCenter under the stable Sefirah bundle identity. Register the delegate at startup, including background startup, so notifications left from a previous process retain their action path. Permission is requested on the first display attempt, without blocking network initialization. Respect the user's system notification preference; do not change it automatically.

Remote notifications retain their device, key, tag and group in native userInfo. Empty tags fall back to the notification key. The adapter supports up to four native buttons, including text reply when available; replies and click indexes use the existing official messages. A default click opens Sefirah. Clipboard actions open only validated web URLs; completed-transfer clicks open an existing file/folder. Call notifications remain informational. Tag/group removal covers pending and delivered notifications; receiving Removed from a peer also removes the matching native entry after the official repository confirms deletion. Intermediate transfer progress stays in the app.

Acceptance on 2026-10-04: a probe on ROG authenticated with its existing PFX against the unchanged Mac certificate and received New plus live Active volume/mute messages. Local volume changes and remote mute actions were checked, then volume 56% and mute=true restored. The final host registered the native notification delegate, kept the data identity, started hidden, reopened its minimized window, and opened/cancelled the native file picker. System Settings showed Sefirah notifications disabled; banner/action/removal UI acceptance awaits permission for a temporary enable-and-restore test. Do not describe registration or request completion alone as notification delivery acceptance.

Shared transport follow-up: NetCoreServer 8.0.7 (package source commit `cb58a43ba0bbce6182d9a5259388c75763d63db7`) returns from its private SslSession.ProcessReceive callback when a connection is no longer handshaked or its stream ID changed, before EndRead can observe the operation result. Reconnect logs show matching unobserved canceled SslStream reads. This is a source-supported explanation, not a verified shared-library fix. No global exception suppression or transport replacement was added.

Closing the window hides it while networking remains active. Status-item clicks and application reopen display the existing window. Login startup uses the established `SefirahLoginLauncher` and `--login-startup` argument.

The General settings page exposes login startup on macOS and Windows. macOS uses login/background wording, and changing the option immediately reconciles its existing per-user LaunchAgent. Linux mount backend settings are shown only on Linux; they have no effect on the macOS external SFTP handler. Windows-only picker HWND initialization must stay behind `#if WINDOWS`, otherwise the exception is caught before the native macOS picker can open.

Maximized login startup on macOS explicitly sizes the native window to its screen's visible frame (excluding the menu bar and Dock). Merely activating/showing the Uno window leaves it at the default 1024x668 frame. Background startup must remain hidden, and ordinary user opens must still restore a minimized or closed-to-background window.
