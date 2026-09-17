# macOS application lifecycle

## Scope

This document defines the macOS login-startup, menu-bar, Dock reopen, minimize, and close behavior for the Uno/Skia desktop build. Read it before changing `App.xaml.cs`, `MacAppLifecycleHelper`, `MacSystemTrayService`, or the macOS bundle assembly path.

## User-visible contract

- A normal Finder, Dock, or explicit application launch shows the main window.
- A login launch applies the saved `StartupOption`:
  - `InTray` starts the services and menu-bar item without ordering the main window onscreen.
  - `Minimized` creates and minimizes the main window.
  - `Maximized` creates and maximizes the main window.
  - `Disabled` does not install a login agent and falls back to a visible window if a stale login argument is received.
- Clicking the menu-bar item, clicking the Dock icon, or opening the already-running app restores a minimized window and orders it to the front.
- The red close button hides the main window while the menu-bar item is available. Explicit Quit still exits the process.

`StartupWindowPolicy` is the platform-independent decision seam. Keep manual launch and login launch separate; never infer a login launch merely because the saved option is `InTray`.

## Login-source contract

The embedded native `SefirahLoginLauncher` opens the main application through `NSWorkspace` with `--login-startup` and without activation. `Program.Main` records that flag in `MacAppLifecycleHelper` before Uno constructs the application. The main application is therefore able to stay hidden only for the login path while retaining normal manual-launch behavior.

The launcher is a signed nested Mach-O with an embedded `CFBundleIdentifier` (`com.castle.sefirah.login`). Keep its Info.plist section and sign it with the same persistent identity as the outer application.

## Registration strategy

The local development bundle is signed by the machine-level `Local Development Code Signing` certificate, which has no Apple Team Identifier. `SMAppService.agent` rejects that identity with `kSMErrorInvalidSignature`, so the installed local build uses a per-user LaunchAgent at:

`~/Library/LaunchAgents/com.castle.sefirah.login.plist`

The native lifecycle bridge writes the plist atomically and reconciles it through `launchctl bootstrap`, `enable`, and `bootout`. The job runs only the embedded `SefirahLoginLauncher`; it does not run the managed executable directly, which prevents duplicate app instances when registration occurs while Sefirah is already running. If an old main-app login item exists, the bridge removes it after the replacement agent is active.

If production later adopts an Apple-issued Development or Developer ID identity with a real Team Identifier, migrating back to `SMAppService` is reasonable, but must be verified against the deployed signature and System Settings state before removing the legacy agent path.

## Native window ownership

Uno 6.7 does not implement `applicationShouldHandleReopen:hasVisibleWindows:` on `UNOApplicationDelegate`. The native lifecycle bridge adds that optional delegate method at runtime and forwards it to the managed dispatcher. The same bridge owns the `NSStatusItem` target/action and performs AppKit window ordering:

- `deminiaturize:` before `makeKeyAndOrderFront:`;
- `orderFrontRegardless` plus application activation for an explicit show;
- `orderOut:` for close-to-background.

Do not replace this with `Window.Activate()` alone. On macOS that does not reliably restore a miniaturized or ordered-out window.

## Packaging invariants

`tools/Build-MacApp.ps1` must:

1. compile `mac-app-lifecycle.m` as `libSefirahMacLifecycle.dylib` beside the managed runtime;
2. compile `mac-login-launcher.m` with `SefirahLoginLauncher-Info.plist` embedded in `__TEXT,__info_plist`;
3. place the launcher at `Contents/Library/LaunchServices/SefirahLoginLauncher`;
4. sign the runtime Mach-O files, login launcher, and outer application with the same stable certificate;
5. verify the result with `codesign --verify --deep --strict`.

The installed application path must remain stable because the user LaunchAgent stores the absolute path to the embedded launcher. Startup reconciliation rewrites the plist on each launch so a supported relocation is repaired automatically.

The deployment process match must allow arguments after `Sefirah.Desktop`. Login-started processes carry `--login-startup`; an end-anchored executable-only pattern leaves the old process alive while replacing its bundle and makes the newly installed build appear to be running when it is not.

## Verification

- `dotnet run --project tests/Sefirah.MacLifecycle.Regression/Sefirah.MacLifecycle.Regression.csproj -c Release`
- publish `Sefirah.Desktop` for `net10.0-desktop/osx-arm64`
- compile both Objective-C sources with `-Wall -Wextra -Werror`
- verify the installed LaunchAgent with `launchctl print gui/$(id -u)/com.castle.sefirah.login` and require `last exit code = 0`
- launch `SefirahLoginLauncher` and verify the managed command line contains `--login-startup` while the home window remains hidden
- open the already-running app from Finder or the Dock and verify the actual home window becomes visible after both minimize and close-to-background
