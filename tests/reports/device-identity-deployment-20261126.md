# Device identity rollout — 2026-11-26 (America/Los_Angeles)

Desktop 3.2.0.18 / macOS build92 and Android 3.2.0.10 / code48 are published as pre-releases and installed in place on all six existing devices. The final management snapshot reports all 15 peer pairs connected on both ends (30/30 directed links), with every saved SPKI pin equal to the corresponding peer's locally reported identity. No proxy configuration was changed.

| Host | Version | Connected peers | Local device ID | SPKI SHA-256 |
| --- | --- | --- | --- | --- |
| mac | 3.2.0.18 | 5/5 | `e7f9327d-fb1d-4f34-b202-95d925ad3d03` | `43132168734C467DAE3972F3384A8CB30C640F97986F5764A66AA3F39766D7E1` |
| omen | 3.2.0.18 | 5/5 | `c5bfc0c8-d340-4bb6-b96d-78726aef7306` | `6C876458A59FB7DF5C7FF2D9B2AAE8381761953E01C0B21E29FAB812E41DAA94` |
| rog | 3.2.0.18 | 5/5 | `18044bb2-53d5-4d6e-85f0-cdec5a8a546b` | `C908CA8B20F75AFCECB14BCDBC7AFA34A5FC13E2950ADD697AA0183DF30D0745` |
| k70 | 3.2.0.10 | 5/5 | `ab2ac3bd9e108d66` | `E2EED5DE30AAA09FDD0B91FDDA45D2A7C4042F3F8F2960A1CD35A22405687DE6` |
| clover | 3.2.0.10 | 5/5 | `c8bc7c872dffc166` | `C6629421F0680BD3AB5C18EEC86C4DD36B16464A89B9306EF7065CE33F97B4DA` |
| liuqin | 3.2.0.10 | 5/5 | `41078bc95e169fb2` | `5C3AD5E9C097AA3D86989F7235DAACF5B127F3001DBE3198B3218074F0A0345A` |

## Pairing repairs

Pad 6 Pro's current pin is `5C3AD5E9C097AA3D86989F7235DAACF5B127F3001DBE3198B3218074F0A0345A`. K70, OMEN and ROG retained the old pin `EFB241831D9A0F50F58039A89539C956AF76D7DEF8E6C3598E76D1981955743E`. These are different SPKIs, proving a key change rather than only different certificate metadata. All three old records were explicitly rebound through API v2 with the expected old pin, actual new public certificate/pin and matching pairwise verification code; the device records and settings were retained. Mac already had the current key.

Pad 4 Plus had no local saved pairings after the database loading barrier completed. Its identity itself was unchanged and the other five devices retained its correct key. Recreated its five local pairings against authenticated existing peers with current matching verification codes. Existing records on those other devices were retained. Temporary synthetic regression peers were deleted.

The supplied Pad 6 Pro install time `2026-11-27 11:02:20` uses UTC+8 and corresponds to `2026-11-26 19:02:20` PST. firstInstallTime equaled lastUpdateTime before this rollout, consistent with a fresh installation record. This does not establish why that earlier installation happened or who initiated it. The old/new SPKI difference establishes identity change. This rollout used `adb install --no-incremental -r --user current` and preserved firstInstallTime, local device IDs, public keys and existing storage grants on all three Android devices.

## Validation

- macOS and Windows each passed 185 production transport regression checks, including 24 concurrent mutually authenticated TLS connections, same-key/different-certificate pins on actual zero-byte and bulk file transfers, different-key rejection and bad rebind credentials.
- Android passed 20 network JVM tests and Release/lint build checks. Its certificate tests generate renewed and unrelated EC certificates.
- Installed macOS and Pad 6 Pro Android each passed 9 live identity/management checks through `tools/test-identity-management.py`: normal pairing, certificate renewal, different-key refusal, three invalid rebind credentials, explicit new-key acceptance, old-key revocation and live unpair followed by unpaired-only discovery. Existing peer pins, local ID and storage location remained unchanged. Generated private keys were removed automatically.
- macOS signature verified with fixed `com.castle.sefirah` and `Local Development Code Signing`; designated requirement still contains the same certificate leaf. Local PFX bytes remained identical.
- Both Windows MSIX installations report 3.2.0.18, Valid signature from CN=AlexbeatsZ.Sefirah, unchanged local PFX bytes and local ID. Installation used one-shot interactive scheduled tasks, which were removed.
- The initial Windows package still had manifest version 3.2.0.16; Windows correctly rejected same-version/different-content installation. Corrected the source manifest, rebuilt, checked embedded AppxManifest.xml=3.2.0.18 and installed the corrected signed package. The failed first attempt changed no installed identity.
- GitHub release assets are uploaded and non-draft: desktop v3.2.0.18 (Mac arm64 ZIP, Windows x64 MSIX and public CER), Android v3.2.0.10 (signed APK).

## Scope and remaining acceptance

Pins derive from stored certificates, so no pairing database migration is needed. Rebind is an explicit local administrative trust decision and preserves configuration; unpair deletes local trust, like the existing UI removal. Private-key loss cannot be repaired by comparing a device ID. Automatic old-key-signed new-key rotation is not implemented in this release.

This verifies deployment, identity persistence on upgrade, actual recovery/revocation and present connectivity. It does not establish overnight/Doze, login/sleep or reboot recovery, or physical UI acceptance. No phone UI input, reboot, radio toggling or DND command was performed for this task.

Detailed public management snapshots and build/signature receipts remain on the Mac in `/tmp/.agents/sefirah-identity-20261126/`; Windows build/install receipts remain under each host's `%LOCALAPPDATA%/Temp/.agents/sefirah-identity-20261126/`. Temporary private keys, copied PFX and duplicate build/install artifacts are removed after verification.
