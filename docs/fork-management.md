# Personal fork and headless management

## Scope and identities

Desktop and Android version 3.2.0.1 use the upstream 3.2.0 protocol baseline plus negotiated controls/presentation v1. Mac retains com.castle.sefirah, Sefirah.Desktop and its existing signing identity. Windows uses AlexbeatsZ.Sefirah, publisher CN=AlexbeatsZ.Sefirah; preserve the old Store LocalState database, PFX and per-device/general settings during migration. Android retains com.castle.sefirah with a durable private release signing key. The K70 has no Sefirah installation after the PixelOS migration, so its fork install starts a new identity and must pair again. Do not overwrite a differently signed Android installation without first planning identity loss and re-pairing.

## Desktop management API v1

Same-user named pipe: sefirah-management-<OS user name>. PipeOptions.CurrentUserOnly enforces OS user identity. There is no TCP listener. One UTF-8 JSON request and newline per connection; response is {ok,data} or {ok:false,error}. Requests are bounded to 8192 characters and a 20 second connection deadline. Use tools/manage.py, locally or through SSH in the logged-in user's account.

Methods: status; connect (deviceId, and address/port for an unknown peer); pair (deviceId, verificationCode); approve/reject (deviceId, verificationCode); disconnect (deviceId); remove (deviceId). Pairing uses the existing authenticated TLS connection and certificate-derived verification code. Status exposes local ID/port, paired/discovered peers, and pending confirmations. Private keys are never returned. Pairing dialogs can be resolved through the API and expire after two minutes. Do not send a blind approval: match current IDs and verification codes on both peers.

The app still needs a logged-in OS session and initialized native host. Headless management means no interactive pairing workflow is required; it is not a new Windows service. Example: uv run --no-project tools/manage.py '{"method":"status"}'. The K70, Mac, OMEN and ROG completed pairing and authenticated connections through these APIs on 2026-11-10.

Windows release packages require the dedicated CN=AlexbeatsZ.Sefirah signer leaf in LocalMachine/TrustedPeople. It is not a CA and must not be installed in the Root store. Keep the signer private key/password outside Git. Install the personal MSIX, stop both app instances, copy the original Store LocalState into the personal package's LocalState, launch in the logged-in interactive session, and verify PFX bytes, local ID, peer certificates and old settings before uninstalling shrimqy.Seki-PhoneLink. Both OMEN and ROG passed these checks and the old Store package was removed. A certificate/signer change requires a separately planned migration.

Mac version 3.2.0.1 build 75 preserves the existing bundle/managed identity and signer. The new K70 identity is ab2ac3bd9e108d66; old desktop settings were carried from 7ec941425ab33391 while each desktop was stopped, and the old pairing entries were removed through the API. The tablet remains offline and was not replaced.

## Reviewed PRs, 2026-11-10

- Desktop #303: merged upstream 2026-10-17. Retain the existing Mac superset, including awaited authentication-before-DeviceInfo, legacy pinned-server acceptance and burst protection while old peers can remain. A direct merge conflicts in NetworkService; keep those compatibility rules.
- Android #91: include. Retries only unexpectedly lost established sessions, with pinned certificates, one loop per peer, bounded backoff and manual disconnect respected. Eleven tests pass. It cannot restart a killed application and does not start retrying every saved peer on boot.
- Desktop #317 / Android #92: include together. Optional capability negotiation, per-peer authorization, bounded UUID cache and explicit results. Android Bluetooth still needs local confirmation; root clipboard does not imply root Bluetooth. Actual native UI and radio transitions require separate acceptance. Integration found an extra AudioDeviceInfo switch case in the Mac branch; remove it and keep the new state handler.
- Desktop #318 / Android #93: include together. Defaults, consent, thumbnail byte/dimension/hash validation and revocation are appropriate. Integration rechecks sharing consent after asynchronous thumbnail acquisition; desktop settings publication is limited to v1 presentation peers. Default sharing remains off. Native UI and wallpaper transfer/revocation still need acceptance.

Upstream PRs remain unchanged. Fork integrations are independent release branches.

## PixelOS background behavior

Observed K70 build: PixelOS_vermeer-16.2-20260619-1539, Android16/API36, APatch. POCO release fingerprint is retained/spoofed and is insufficient to identify the ROM. Cached processes are actually frozen; activity settings show max_cached_processes=1024 and freezer_cutoff_adj=900. These are observations, not values changed by the fork.

PixelOS sixteen-qpr2's manifest selects its frameworks/base and Settings repositories; the lmkd path remains the AOSP project. Framework ActivityManagerConstants uses DEFAULT_MAX_CACHED_PROCESSES=1024. BatteryOptimizeUtils implements unrestricted as app-op allowed plus power allowlist membership. The system still has distinct Doze/App Standby, cached-process freezer, lmkd pressure kills and explicit user stop states. Root alone does not exempt an ordinary app from them.

Current Sefirah already has connectedDevice foreground service, START_STICKY, BOOT_COMPLETED/MY_PACKAGE_REPLACED and a battery exemption request. First use ordinary unrestricted battery configuration and real exit records. Root clipboard worker exits with its host and does not resurrect a deliberately stopped app. Do not globally disable Doze/freezer/lmkd or pin all processes' oom_score_adj. A future opt-in supervisor must persist user stop intent and distinguish crash/low-memory recovery from force-stop; none is installed by this release.

Sources: https://github.com/PixelOS-AOSP/android_manifest/blob/sixteen-qpr2/snippets/pixelos.xml ; https://github.com/PixelOS-AOSP/android_frameworks_base/blob/sixteen-qpr2/services/core/java/com/android/server/am/ActivityManagerConstants.java ; https://github.com/PixelOS-AOSP/android_packages_apps_Settings/blob/sixteen-qpr2/src/com/android/settings/fuelgauge/BatteryOptimizeUtils.java ; https://source.android.com/docs/core/perf/cached-apps-freezer ; https://source.android.com/docs/core/perf/lmkd ; https://developer.android.com/training/monitoring-device-state/doze-standby . The exact ROM manifest commit for the installed build is not known.
