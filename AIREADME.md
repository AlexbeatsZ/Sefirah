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
- Windows 3.0.0.5 presents Bluetooth devices in three selected-endpoint sections, supports per-headset visibility, and exposes matching `view`, `disconnect`, and `visibility` CLI commands for deterministic testing.
- Windows cannot directly connect or disconnect a single paired audio device through the public API. Handoff uses a short target-radio off/on cycle only when per-device connection control is unavailable; direct Disconnect refuses to disable the whole PC radio.
- Updating 3.0.0.2 through 3.0.0.5 preserved the fork package identity and preserved `sefirah.db`, `Sefirah.pfx`, and `user_settings.json` byte-for-byte at every installation boundary. Backups are under `%LOCALAPPDATA%\Temp\.agents\Sefirah`.
- QCY AilyBuds Lite was physically validated PC to Redmi K70 and Redmi K70 to PC. Both radios returned enabled, the final connection was restored to the PC, and both desktop and Android three-section UIs were visually verified.
- Windows 3.0.0.9 localizes the complete desktop resource set into Simplified Chinese and aligns the Bluetooth module hierarchy with Notifications: the module heading remains outside the cards, while the three device sections use separate cards and per-device context actions.
- Removing `Language` from `AppxBundleAutoResourcePackageQualifiers` requires the complete `AppxDefaultResourceQualifiers` union, including the original `Scale=200`. Omitting it collapsed the scale resource-package graph and caused an in-place update to fail in Windows MRT `SystemRegisterRemove` with `0x80073CF9`/`0x8007000D`.
- The corrected 3.0.0.9 bundle embeds all 15 UI languages in the main package while preserving the prior `split.scale-100/125/150/300/400` graph. Updating from 3.0.0.7 succeeded and preserved `sefirah.db`, `Sefirah.pfx`, and `user_settings.json` byte-for-byte; the backup is under `%LOCALAPPDATA%\Temp\.agents\Sefirah\pre-ui-3.0.0.9-20260718-231936\LocalState`.
- The official 2.4.0 server database is schema 3. sqlite-net cannot add the schema 5 primary-key columns during `CreateTable`, so legacy content tables now use an explicit transactional rebuild migration; paired-device, local-device, and certificate rows are left in place.
- The schema 3 to 5 migration passes both a synthetic regression fixture and a read-only copy of the server database containing 3 pairings, 261 conversations, and 612 messages.
- Server `META-ROGALLY` now runs fork package `Meta.Sefirah.Fork` 3.0.0.10 in console session 1. The original Store package was removed only after a complete LocalState backup, and the fork PFX hash remained `7DD1AD685076DCF6362B186D194558CD806344F55280847654FB4F339286791C`. The immediate pre-3.0.0.10 backup is `%LOCALAPPDATA%\Temp\.agents\Sefirah\server-deploy\pre-3.0.0.10-20260718-235628\LocalState` on the server.
- A stable peer connection needs deterministic collision handling when both Windows peers initiate simultaneously. Choose one direction from the two device IDs, authenticate the accepted socket atomically, keep the survivor alive with TCP keepalive, and ignore stale disconnect callbacks belonging to a replaced socket.
- Existing paired inbound connections must receive both the authentication response and current `DeviceInfo`; otherwise one side can look connected while the initiating side waits, lacks capabilities, and repeatedly reconnects.
- Remote `ActionInfo` execution is now denied unless the sender explicitly advertises `remote-actions-controller-v1`; Bluetooth catalog and command messages are independently gated by `bluetooth-handoff-v1`.
- Desktop peers can legitimately send `BluetoothHandoffConfiguration` and `BluetoothHandoffState`, but these are companion-facing state messages and should be recognized without mutating the receiving desktop's local configuration.
- Windows 3.0.0.27 handles Bluetooth catalog requests and commands on both desktop peers and routes session-level socket failures back to the owning `ServerSession` for immediate cleanup. `NotConnected` on an already-closed transient session is normal close-race telemetry and is kept at debug level; other server and session socket faults remain warnings/errors.
- 3.0.0.27 was installed on `Meta-OMEN` and `META-ROGALLY` with each machine's `sefirah.db`, `Sefirah.pfx`, and `user_settings.json` preserved byte-for-byte at every package update boundary. A live `sefirahctl bluetooth list Meta-ROG` returned the remote catalog, and a 75-second post-upgrade observation contained no new warning/error, unknown-message, or `NotConnected` entries.

# Task Board

- [x] Fork repository and configure `origin`/`upstream` remotes.
- [x] Add headset capability protocol and Windows handoff coordinator.
- [x] Add Windows Bluetooth device provider with per-device control first and radio-cycle rollback fallback.
- [x] Add desktop discovery and handoff UI.
- [x] Add non-destructive schema upgrades and validated Store-data migration tooling.
- [x] Add a reproducible signed x64 MSIX Bundle installer with UAC certificate trust, automatic first-run Store data migration, and an in-project artifact/signing layout.
- [x] Add and validate a directly runnable, Authenticode-signed x64 EXE installer that performs a clean fork install without Store-data migration.
- [x] Add a current-user-only command-line control API and validate status, Bluetooth catalogs, discovery, and saved configuration end to end.
- [x] Redesign the Windows Bluetooth card around the selected endpoint with three sections, expandable actions, and visibility settings.
- [x] Extend the CLI with grouped UI-state, direct-disconnect, and visibility commands; physically validate AilyBuds handoff in both directions.
- [x] Localize the Windows UI into Simplified Chinese, align Bluetooth with the Notifications visual hierarchy, reuse the device selector for handoff targets, and ship the data-preserving 3.0.0.9 update.
- [x] Add an explicit schema 3 to 5 primary-key migration with regression coverage and deploy the data-preserving 3.0.0.10 fork to the Windows server.
- [x] Stabilize simultaneous Windows peer connections, add capability-gated remote actions, route desktop Bluetooth catalog/command messages, clean failed sessions, and deploy the data-preserving 3.0.0.27 update to both Windows peers.
- [ ] Replace the tablet installation after its USB ADB interface is enabled and authorized; Windows currently sees Xiaomi Pad 6 Pro only as an MTP/WPD device.
- [ ] Extend the control API with an explicit allowlist for future non-Bluetooth app actions; never expose arbitrary shell execution through the pipe.
- [ ] Complete secure Android first-time re-enrollment for the signing-key transition.
- [ ] Add automated tests and validate QCY-T13 and QCY AilyBuds Lite in all three-device directions.
- [x] Commit and push the feature branch.
# 2026-07-24 Fork Bluetooth Catalog and Connection Work

## Project Goal

- 蓝牙卡片默认显示全部已配对设备，并提供“全部 / 仅耳机”筛选。
- 统一电脑、Windows 服务器、平板和手机的蓝牙端点目录与切换协议。

## Lessons Learned

- Windows 设备目录需要枚举并合并 Classic 与 BLE；规范化蓝牙地址比名称匹配可靠。
- `SendAsync` 已复制到 NetCoreServer 内部缓冲；原始回调 buffer 生命周期不是平板断流根因。
- `SendControlAndFlush` 的“未清空”警告只说明 250 ms 内发送队列仍有数据，不等同于心跳未排队。
- Android 回调内同步执行 ADB TCP 可达性探测会阻塞 socket 处理，必须按设备设置在后台去重执行。
- 平板断流在同一设备的无线 ADB TLS 上也复现，因此不能继续只修改 Windows 心跳来猜根因。

## Task Board

- [done] 全设备蓝牙目录、Classic/BLE 合并、地址身份与精确耳机分类。
- [done] UI 和 CLI 支持默认 `all` 与可选 `headsets`。
- [done] Android 心跳响应、主动心跳、串行收包和 ADB TCP 回调解耦。
- [done] `Sefirah.BluetoothCatalog.Regression` 通过。
- [done] Windows v40 构建、打包并部署至电脑和服务器；服务器证书保留。
- [paused] Xiaomi Pad 6 Pro 的 20–30 秒系统层断流，留待取得稳定 USB logcat 后继续。

## Evidence

- Bundle SHA-256: `DFAD6466D3E373AE76C57174274D7F8F13698BF94EAD01DA05B72BBBB4ECBF2B`
- 实机“全部”目录包含键盘、鼠标、手写笔和耳机；“仅耳机”只保留 QCY AilyBuds Lite 与 QCY-T13。
- 服务器 v40 安装状态 `Ok`，PFX 文件哈希保持 `7DD1AD685076DCF6362B186D194558CD806344F55280847654FB4F339286791C`。
