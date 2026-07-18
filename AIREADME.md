# Project Goal

- Maintain a long-lived personal fork of Sefirah while keeping changes suitable for upstream pull requests.
- Coordinate one-click Bluetooth headset handoff between Windows, Redmi K70, and Xiaomi Pad 6 Pro.
- Preserve existing device records and settings across fork updates; the first Android migration uses a one-time secure re-enrollment because the official Android signing key and Android Keystore private key are unavailable.

# Lessons Learned

- Upstream `v3.0.0` already pairs the phone to Windows for Bluetooth calling, but it does not control arbitrary Bluetooth audio-device connections.
- The installed Microsoft Store build is `2.4.0`; its pairing database and exportable TLS identity are `LocalState\sefirah.db` and `LocalState\Sefirah.pfx`.
- Never modify or replace live Store data directly. Before migration, stop the app with user confirmation, copy the database, PFX, and settings into `%LOCALAPPDATA%\Temp\.agents\`, then validate the copy.
- Old clients must be capability-gated before receiving new polymorphic socket message types.
- The installed Store database is schema version 3. Upstream v3 declares schema version 5 but has no v3-to-v5 migration and previously used destructive fallback; the fork now backs up and performs additive reconciliation instead.
- Desktop and Windows targets compile without restoring packages; existing upstream warnings remain.
- The Microsoft Store package publisher is Microsoft-controlled, so a self-signed fork cannot update it in place. The fork uses the side-by-side identity `Meta.Sefirah.Fork`; its installer backs up and migrates the complete Store `LocalState` before first launch.
- Windows fork signing material lives only in the ignored project-local `.signing` directory. The PFX password is DPAPI-protected for the current Windows user and is passed to MSBuild through an environment property, not process arguments. Back up the whole directory before deleting the checkout.
- The x64 sideload build can report APPX certificate-store warnings because the project key is deliberately not imported during builds. The build script independently rejects a bundle whose Authenticode signer thumbprint does not match the project certificate.
- The user-facing Windows artifact is now a self-contained x64 EXE bootstrapper. It embeds the signed bundle and public certificate, requests UAC, installs the package, and intentionally does not import Microsoft Store data; only future updates of the same fork identity preserve fork `LocalState` in place.
- Windows 3.0.0.2 exposes a current-user-only named-pipe control API (`Sefirah.Control.v1`) and the `sefirahctl` client. It supports status, endpoint/catalog queries, discovery, saved headset configuration, direct endpoint commands, and coordinated handoff without UI automation.
- Updating the fork package from 3.0.0.1 to 3.0.0.2 preserved `sefirah.db`, `Sefirah.pfx`, and `user_settings.json` byte-for-byte. The pre-update copy is under `%LOCALAPPDATA%\Temp\.agents\Sefirah\pre-control-api-3.0.0.2\LocalState`.
- `sefirahctl bluetooth discover` was physically validated against the installed Windows app and Redmi K70: both QCY headsets were matched across PC and phone, and the active endpoint was reported correctly.

# Task Board

- [x] Fork repository and configure `origin`/`upstream` remotes.
- [x] Add headset capability protocol and Windows handoff coordinator.
- [x] Add Windows Bluetooth device provider with per-device control first and radio-cycle rollback fallback.
- [x] Add desktop discovery and handoff UI.
- [x] Add non-destructive schema upgrades and validated Store-data migration tooling.
- [x] Add a reproducible signed x64 MSIX Bundle installer with UAC certificate trust, automatic first-run Store data migration, and an in-project artifact/signing layout.
- [x] Add and validate a directly runnable, Authenticode-signed x64 EXE installer that performs a clean fork install without Store-data migration.
- [x] Add a current-user-only command-line control API and validate status, Bluetooth catalogs, discovery, and saved configuration end to end.
- [ ] Extend the control API with an explicit allowlist for future non-Bluetooth app actions; never expose arbitrary shell execution through the pipe.
- [ ] Complete secure Android first-time re-enrollment for the signing-key transition.
- [ ] Add automated tests and validate QCY-T13 and QCY AilyBuds Lite in all three-device directions.
- [x] Commit and push the feature branch.
