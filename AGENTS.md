# Sefirah Windows Fork Instructions

## Repository and Branch Policy

- This is AlexbeatsZ's Windows fork. Keep `origin` pointed at `AlexbeatsZ/Sefirah` and `upstream` at `shrimqy/Sefirah`.
- Keep changes reviewable for upstream where practical, but preserve fork-specific packaging, control API, and device-coordination behavior when required.
- Do not overwrite or discard unrelated dirty work. Commit and push only the files in the current task.

## Product Invariants

- Preserve user data, pairings, certificates, settings, and Cloud Files state across upgrades. Schema changes require explicit migrations and regression coverage.
- Bluetooth devices are identified by normalized address. Merge Classic and BLE records; classify devices by capability/category rather than name substrings.
- The catalog defaults to all paired devices; headset-only is an optional filter.
- The selected endpoint drives the Bluetooth UI and coordinator. Keep UI state, CLI state, and protocol state consistent.
- An accepted connect/disconnect request is not final success. Wait for observable state with bounded timeouts. If Android per-device connect fails, perform the defined target-radio recovery before waiting again.
- Serialize cross-peer handoff and use deterministic connection direction plus bounded retry/backoff to avoid simultaneous connection storms.
- Control-pipe operations are current-user-only and explicitly allowlisted. Never add arbitrary shell execution.
- Protocol changes must be capability-gated so older Android clients remain usable.

## Remote Storage and Security

- Hold the real lifetime task for every async provider, watcher, and queue. Do not wrap `async` delegates with `new Task` or unwrapped `StartNew`.
- Replacement of a sync root must await the old generation, serialize per root, and remove state only when the completing object is still the registered generation.
- Refresh stored SFTP context when credentials change and restart the provider safely.
- Orphan cleanup and device-rename reconciliation must never delete hydrated or unrelated local files.
- Treat Android remote storage as untrusted until it authenticates the paired desktop key and confines every operation to canonical selected-share paths.

## Packaging and Deployment

- Use the repository's reproducible signed x64 package/installer flow. Keep signing materials out of Git.
- The app package is built self-contained (`-p:SelfContained=true` in `tools/Build-SideloadPackage.ps1`): the Windows Server peer has no .NET 10 desktop runtime, so framework-dependent packages cannot start there. Verify `hostfxr.dll` is inside the MSIX when deployment fails to launch. The staged `sefirahctl.exe` stays framework-dependent; run it from a machine with .NET 10.
- Before any upgrade, verify the installed package identity and back up the relevant LocalState under `%LOCALAPPDATA%\Temp\.agents\` when rollback risk warrants it.
- After deployment, verify package status, preserved data, and the changed behavior. Do not rely on an old version number, hash, PID, endpoint, or radio state recorded in documentation.

## Verification

Run the smallest relevant regression suite first, then the complete affected Windows build/tests. Bluetooth changes must cover catalog identity/filtering and coordinator recovery. Cloud Files changes must cover provider replacement, cancellation, reconnect, and safe root reconciliation. Connection framing/authentication changes must run `Sefirah.ConnectionAuthentication.Regression` in addition to `Sefirah.RemoteActionSafety.Regression`.

## Design References

- [`docs/design/bluetooth-handoff-ui.md`](docs/design/bluetooth-handoff-ui.md): Bluetooth catalog grouping, labels, endpoint choices, legacy compatibility, and action semantics. Read it before changing the handoff page or view model.

## Active Work

- Add safe orphan-sync-root recovery and device-rename reconciliation.
- Add selected remote shares and a unified shortcut hub while preserving on-demand hydration.
- Extend future non-Bluetooth controls only through an explicit allowlist and capability checks.
- Add automated multi-peer/headset coverage and repeat physical validation when devices are reachable.

## Current State

The feature branch contains the fork's Bluetooth catalog/handoff, control API, signed packaging, and Cloud Files provider work. Windows package `3.1.0.12` uses the Windows symbol font for app-owned glyph icons. The macOS/Skia bundle ships Uno.Fonts.Fluent; its native launcher changes the process working directory to `Contents/Resources/runtime` before `exec`, startup registers the bundled Symbols face with CoreText at process scope before constructing UI, and packaging embeds the branded `Contents/Resources/AppIcon.icns` declared by `CFBundleIconFile`. Its Bluetooth catalog reads the actual `system_profiler` connected/disconnected groups, treats native `IOBluetooth` per-device control independently from optional `blueutil`, and preserves stable headset rows across periodic refreshes. Windows handles the system close action through cancellable `AppWindow.Closing` so the main window reliably hides to the tray, and reconciles the packaged startup task against `StartupOption` on every launch; the default `InTray` setting therefore starts at login without showing the window. The Windows frontend was rewritten on native WinUI 3 + Windows App SDK 2.4 (Uno Platform removed; the Skia/Linux desktop TFM code stays in `Platforms/Desktop` but is excluded from compilation). The title bar is a window-level control, the app host is a plain `Microsoft.Extensions.Hosting` Generic Host with Serilog, and localization goes through `ResourceLocalizer`/`ResourceString` over the packaged resw map. Bluetooth endpoints whose privileged controller is unavailable remain visible with an actionable persistent error, but their mutating actions are disabled. The fork has no built-in Store/upstream automatic update check, update toast, or update action; repository and issue links target the AlexbeatsZ forks. The branch also carries selected upstream v3.0.1 correctness fixes while retaining the fork's capability-gated mixed-version protocol and deterministic collision policy. Installed versions and physical-device state are volatile; recheck them before deployment or hardware validation. Use Git history and tests for completed implementation evidence rather than adding completed task boards here.

## Durable Lessons

- macOS `system_profiler` exposes Bluetooth connection state in the singular group names
  `device_connected` and `device_not_connected`; it does not reliably add a connection field to
  each device. Keep optional helper discovery separate from native framework availability, or an
  absent `blueutil` can mask a working `IOBluetooth` fallback.
- Native `IOBluetooth` access without `NSBluetoothAlwaysUsageDescription` does not produce a
  catchable managed exception: TCC kills the macOS process. Keep the usage string in every bundle
  assembly path and validate the packaged `Info.plist` before physical-device tests.
- Ad-hoc macOS signatures produce a cdhash-based designated requirement that changes on rebuild,
  causing TCC to treat later builds as a different Bluetooth client. Sign the bundle and nested
  Mach-O files with the machine-level persistent `Local Development Code Signing` identity (shared
  with other local projects such as `codex-plus`); the private key remains in the local login
  keychain and is never committed. Sharing one certificate is safe because the requirement's
  `identifier` is per-bundle, so each app's grants stay independent. Recreating the identity on a
  new machine: see `~/.agents/knowledge/macos-tcc-permissions.md`.
- `IOBluetoothDevice.closeConnection` can block indefinitely when the device is already
  disconnected. Read `isConnected` first and treat an already-satisfied connect/disconnect as
  success before invoking the transition selector. Run the native transition through a bounded
  signed helper subprocess so a stuck private API cannot consume the app's single control worker
  indefinitely; JXA/`osascript` does not reliably inherit the parent app's Bluetooth TCC identity.
- Clearing and repopulating a frequently refreshed collection under Uno/Skia `ItemsRepeater` can
  recycle a button with stale `PointerOver` state. Reconcile rows by stable device identity and
  explicitly restore `Normal` on pointer exit, load, and flyout close.
- Uno's macOS resource generator treats a backslash in a linked PRI resource path as a literal
  filename character. Use `/` in the `Link` path so local macOS builds generate `Strings/<locale>`
  instead of `Strings\\<locale>`.
- Native WinUI 3 close-to-tray must intercept the cancellable `AppWindow.Closing` event and set
  `AppWindowClosingEventArgs.Cancel` before hiding. `Window.Closed` is too late to provide reliable
  caption-button close semantics.
- On Uno/Skia macOS, the launcher must `chdir` to `Contents/Resources/runtime` before `exec` so `ms-appx:///Uno.Fonts.Fluent/...` resolves, but correct resource lookup alone is not sufficient. The stream-backed Symbols typeface can report the right family, glyph coverage, and shaping IDs yet still draw private-use glyphs through a fallback as question-mark boxes. After `App.InitializeComponent()`, register the bundled TTF with CoreText at process scope and set `FeatureConfiguration.Font.SymbolsFont` to `Symbols` before constructing UI. Verify packaged icon pixels, not only font metadata.
- A `CFBundleIconFile` entry does not supply an icon by itself. Hand-assembled macOS bundles must also copy the matching multi-resolution `.icns` into `Contents/Resources`; otherwise Finder and the Dock display the generic application placeholder.
- Packaged startup registration is stateful outside the app settings file. Reconcile
  `StartupTask.State` with the desired `StartupOption` on every launch instead of guarding it with
  a one-time LocalSettings marker; log the resulting runtime state so deployment can be verified.
- NetCoreServer `SendAsync` means that a complete frame was accepted into its locked internal
  buffer, not that the network is empty. Waiting for global drain after every application frame
  can starve heartbeats. Keep application traffic in a bounded single-writer queue, let small
  control frames enter the transport buffer directly, and configure a transport buffer limit.
- TCP can deliver authentication and application frames in the same read. Buffer later frames
  per connection until authentication completes, preserve their arrival order, and discard the
  buffered generation if that connection is superseded or cancelled.
- A capability-advertising endpoint can still have an unavailable runtime dependency. Preserve
  the endpoint and its diagnostic state in the catalog, but keep its actions disabled until a
  later successful refresh; do not replace the actionable error with a generic device count.
- Native MRT (`ResourceLoader.GetString`) throws COMException 0x80073B17 "NamedResource not
  found" for unknown keys, and resw dot-keys must be looked up with slashes
  (`Connected.Text` → `Connected/Text`). `ResourceLocalizer` and the `ResourceString` markup
  extension normalize and fall back to the key; missing-key exceptions there kill app startup
  silently because activation is fire-and-forget — keep the failure logging wrapper in
  `App.OnLaunched`.
- WASDK 2.x XAML compilation (`Microsoft.WindowsAppSDK.WinUI` 2.3.6) moved `Page` to
  `Microsoft.UI.Xaml.Controls`; `XamlPreCompile` builds a temp assembly whose csc failures are
  swallowed as WMC9999/WMC1509 — diagnose with `-v:diag` and read the hidden `error CS` lines.
- The single-instance handshake reads `INSTANCE_ACTIVE` from LocalSettings. PIDs are reused by
  unrelated processes and crashed sessions leave stale values, so the redirect target must be
  both alive AND named `Sefirah`, with a bounded `CoWaitForMultipleObjects` timeout; otherwise
  startup deadlocks before any window exists.
- `ExtendsContentIntoTitleBar` only removes the system title bar; the replacement control must
  also be registered with `Window.SetTitleBar` or it is not a draggable caption region. Keep the
  root frame idempotent across activations, and log navigation/startup failures instead of
  constructing discarded exceptions or rethrowing into an unobserved fire-and-forget task.
- When multiple desktop peers connect directly, each peer proactively sends periodic heartbeats.
  If a receiver replies unconditionally to incoming heartbeats with an immediate heartbeat reply,
  a line-rate ping-pong cascade develops (filling gigabytes of logs and consuming 100% CPU).
  Incoming heartbeat replies must be rate-limited (e.g. minimum 5s interval per connection) to break
  infinite echo storms while preserving Android timeout recovery.
- AppX package installation/updates via remote OpenSSH fail with `0x80070005` (Access Denied /
  `Failed to reach state PackagesInUseClosed`) when using `-ForceApplicationShutdown`, because
  remote SSH sessions lack an interactive desktop token to activate Process Lifecycle Manager (PLM).
  Deploy updates on headless/remote Windows machines via an interactive scheduled task
  (`schtasks /create ... /it /f`) executed under the logged-on console user session.
