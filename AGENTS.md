# Sefirah personal fork

## Goal
Maintain the user's personal fork with shared Windows/macOS transport and Android interoperability. Origin AlexbeatsZ/Sefirah, branch codex/personal-fork. Preserve installed identities and pairing data.

## Current State
- Desktop 3.2.0.14 / Mac build88 is released and running on Mac, OMEN and ROG. Ringer commands require explicit button activation. Applying the remote report to the UI never sends a command. K70 is on Android 3.2.0.7/code45 and reconnected to all three hosts and Pad 4 Plus without new ringer/DND writes. Both tablets lack an ADB transport and still need the Android update.
- 2026-11-17 desktop 3.2.0.12 / Mac build86 fixes wireless ADB restart loops after Android reflashing. USB automation requires a verified paired app ID (identity file or live authenticated endpoint), reads live Wi-Fi IP/TCP port, and serializes mode setup with bounded retries. Read docs/design/wireless-adb.md before editing this flow.
- Desktop 3.2.0.14 includes the 2026-11-19 ADB device-list and scrcpy quoting fixes for mDNS serials with spaces such as `adb-x (2)._adb-tls-connect._tcp`. ADB41 checks pass on Windows and macOS.
- ADB32, transport164, controls7 and presentation125 regression checks pass on Windows. Windows signature Valid and Mac strict signature verification passed. OMEN/ROG/Mac all run3.2.0.12 (Mac build86); identities/peers retained. Tablet auto-connect is enabled and its150-second USB-mode recovery test passed with exactly one TCP enable and stable USB+wireless afterward.
- Windows x64 self-contained MSIX built on OMEN, signature Valid, signer CN=AlexbeatsZ.Sefirah. Release v3.2.0.14 contains the Windows MSIX, Windows public CER and macOS arm64 ZIP. Mac bundle passed strict signature validation.
- Native Windows/macOS transport regression passed164 checks on the earlier transport rollout. Mac passed164 again for 3.2.0.14.
- The 2026-11-17 desktop deployment and tablet USB/wireless ADB recovery acceptance passed. Broader login/sleep/background acceptance remains separate.
- Private signing key and password stay in .private. The 3.2.0.14 Windows build log is %TEMP%/.agents/sefirah-one-shot-v14-signed/publish.log on OMEN.

## Active Work
- Verify the one-shot ringer changes on physical devices after upgrading the affected peers.
- Physical device upgrade, login/sleep recovery, locked/background continuity and UI acceptance pending.
- Broader schema generation, unified management CLI, UI-dispatch injection and full PairedDevice separation remain future architecture work.

## Build / Run / Test
- Read docs/device-protocol.md and docs/design/desktop-link-auth.md before changing transport/authentication.
- dotnet run --project tests/Sefirah.Adb.Regression -c Release
- dotnet run --project tests/Sefirah.Transport.Regression -c Release
- Publish src/Sefirah/Sefirah.csproj Release net10.0-windows10.0.26100 win-x64 with Platform=x64, SelfContained=true, WindowsAppSDKSelfContained=true, TargetFrameworks restricted to Windows, GenerateAppxPackageOnBuild=true. Use existing .private signer and verify Get-AuthenticodeSignature.
- The 3.2.0.14 OMEN build driver is %TEMP%/.agents/sefirah-one-shot-sign.ps1. It builds and signs against the task's local NuGet source.

## Durable Lessons
- Schannel regression certificates need PFX-imported private keys; CreateSelfSigned's ephemeral CNG key failed before certificate validation.
- Observable PairedDevice properties need the UI dispatcher on Windows.
- Preserve per-transport TLS binding, ordered dispatch, TCP backpressure and single-owner close. Run transport regressions on Windows and macOS after runtime/library upgrades.

## Docs Index
- docs/design/wireless-adb.md covers identity association, live USB network state and idempotent TCP setup.
- docs/device-protocol.md covers transport ownership, authentication, framing, transfers, discovery, authorization and compatibility fixtures.
- docs/design/desktop-link-auth.md covers desktop link recovery and legacy authentication.
- docs/design/official-migration.md covers identities and packaging.
- docs/device-controls.md and docs/device-presentation.md cover negotiated features.
