# Sefirah personal fork

## Goal
Maintain the user's personal fork with shared Windows/macOS transport and Android interoperability. Origin AlexbeatsZ/Sefirah, branch codex/personal-fork. Preserve installed identities and pairing data.

## Current State
- 2026-11-26 desktop 3.2.0.18 / Mac build92 installed on Mac, OMEN and ROG; Android 3.2.0.10/code48 installed on K70, Pad 6 Pro and Pad 4 Plus. Both releases are published with signed assets. All six retain their local keys/IDs; final API check is 30/30 directed connections with matching current SPKI pins.
- Windows/macOS control and independent transfer TLS pin SHA-256 of DER SPKI. Existing saved certificates derive their pins without a database migration. API v2 adds local unpair/remove, explicit configuration-preserving rebind, public local certificate and peer fingerprints. Read docs/design/device-identity.md before editing identity/recovery.
- K70/OMEN/ROG were explicitly rebound from Pad 6 Pro's old public key to its verified current one. Pad 4 Plus's five missing local pairings were restored; other hosts kept their records/settings. This rollout preserved PFX bytes and Android first-install times/public keys. Report: tests/reports/device-identity-deployment-20261126.md.
- Mac and native Windows each pass 185 transport checks. Mac and Android real API synthetic-peer suites each pass 9 checks, including certificate renewal, old-key revocation and live unpair. Mac fixed signing certificate/identifier and Windows CN=AlexbeatsZ.Sefirah remain unchanged.
- Earlier port-reservation, ordered transport, manual disconnect/resume, one-shot controls and wireless ADB fixes remain included. Windows installs use a one-shot Interactive task with the actual WindowsIdentity user, not $env:USERDOMAIN; AppxBundle=Never produces an MSIX.

## Active Work
- Verify the one-shot ringer changes on physical devices after upgrading the affected peers.
- Login/sleep recovery, locked/background continuity and physical UI acceptance remain pending; all six device upgrades and current pairing recovery are complete.
- Broader schema generation, unified management CLI, UI-dispatch injection and full PairedDevice separation remain future architecture work.

## Build / Run / Test
- Read docs/device-protocol.md and docs/design/desktop-link-auth.md before changing transport/authentication.
- dotnet run --project tests/Sefirah.Adb.Regression -c Release
- dotnet run --project tests/Sefirah.Transport.Regression -c Release
- Publish src/Sefirah/Sefirah.csproj Release net10.0-windows10.0.26100 win-x64 with Platform=x64, SelfContained=true, WindowsAppSDKSelfContained=true, TargetFrameworks restricted to Windows, GenerateAppxPackageOnBuild=true. Use existing .private signer and verify Get-AuthenticodeSignature.
- When bumping a desktop release, update both src/Directory.Build.props and src/Sefirah/Package.appxmanifest; verify the embedded MSIX manifest version before installation. Windows AppxBundle=Never, signing disabled at publish, then signtool with existing .private signer.

## Durable Lessons
- Schannel regression certificates need PFX-imported private keys; CreateSelfSigned's ephemeral CNG key failed before certificate validation.
- Observable PairedDevice properties need the UI dispatcher on Windows.
- macOS allows several listeners in one process to bind the same TCP port and only one receives connections; never rely on bind failure alone to pick a free transfer port.
- Preserve per-transport TLS binding, ordered dispatch, TCP backpressure and single-owner close. Run transport regressions on Windows and macOS after runtime/library upgrades.

## Docs Index
- docs/design/device-identity.md covers SPKI pins, persistent keys, local unpair/rebind and asymmetric pairing recovery.
- docs/design/wireless-adb.md covers identity association, live USB network state and idempotent TCP setup.
- docs/device-protocol.md covers transport ownership, authentication, framing, transfers, discovery, authorization and compatibility fixtures.
- docs/design/desktop-link-auth.md covers desktop link recovery and legacy authentication.
- docs/design/official-migration.md covers identities and packaging.
- docs/device-controls.md and docs/device-presentation.md cover negotiated features.
