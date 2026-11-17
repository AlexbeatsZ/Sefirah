# Sefirah personal fork

## Goal
Maintain the user's personal fork with shared Windows/macOS transport and Android interoperability. Origin AlexbeatsZ/Sefirah, branch codex/personal-fork. Preserve installed identities and pairing data.

## Current State
- 2026-11-17 desktop 3.2.0.11 / Mac build85 fixes wireless ADB restart loops after Android reflashing. USB automation requires the actual paired app ID, reads live Wi-Fi IP/TCP port, and serializes mode setup with bounded retries. Read docs/design/wireless-adb.md before editing this flow.
- ADB28, transport164, controls7 and presentation125 regression checks pass on Windows. Signed release packaging and three-desktop deployment are in progress.
- Windows x64 self-contained MSIX built on this OMEN checkout. Signature Valid, signer CN=AlexbeatsZ.Sefirah. Release assets are dist/Sefirah-Windows-3.2.0.10-x64.msix and dist/Sefirah-Windows.cer.
- Previous native Windows/macOS transport regression passed164 checks. Android v3.2.0.6 remains the current Android build.
- Installed-device acceptance is pending. Actual installed-device inventory belongs to the existing cross-device task log on Mac.
- Private signing key and password stay in .private. Build staging/logs are %TEMP%/.agents/sefirah-architecture-20261113.

## Active Work
- Physical device upgrade, login/sleep recovery, locked/background continuity and UI acceptance pending.
- Broader schema generation, unified management CLI, UI-dispatch injection and full PairedDevice separation remain future architecture work.

## Build / Run / Test
- Read docs/device-protocol.md and docs/design/desktop-link-auth.md before changing transport/authentication.
- dotnet run --project tests/Sefirah.Adb.Regression -c Release
- dotnet run --project tests/Sefirah.Transport.Regression -c Release
- Publish src/Sefirah/Sefirah.csproj Release net10.0-windows10.0.26100 win-x64 with Platform=x64, SelfContained=true, WindowsAppSDKSelfContained=true, TargetFrameworks restricted to Windows, GenerateAppxPackageOnBuild=true. Use existing .private signer and verify Get-AuthenticodeSignature.
- The final build driver is %TEMP%/.agents/sefirah-architecture-build.ps1. It tests/builds/signs and writes dist; it does not install a package.

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
