# Two-way desktop link authentication

Read this before changing `NetworkService` authentication, connection replacement or message framing.

## Problem in official ≤3.2.0

A link has one TLS client and one server. The client sends `Authentication`. The server verifies the client certificate against the stored paired certificate (`HandleServerSessionAuthentication` → `AuthenticatePairedDeviceClient`) and marks the client connected. A desktop server never replied for a paired device; `SendAuthenticationMessage` ran only for discovered devices. The connecting desktop therefore never ran `HandleServerAuthentication`, so it never marked the server connected. `ConnectCore` sets it back to Disconnected right after the handshake. Clipboard and other broadcasts go only to `IsConnected` devices, so only the server-to-client direction worked.

Android servers reply with `Authentication` (`authenticatePairedDevice` in the Android `NetworkService`), so phone links work. Android as a client marks itself connected without waiting for a reply and ignores any later `Authentication` (`else -> {}` in its `handleMessage`).

The connecting desktop stayed Disconnected, so each later mDNS/UDP discovery event started a new client connection. The server then replaced its session. Mac logs from 2026-10-16 12:04:05 show OMEN and ROG each connecting twice within 350 ms.

## Fix (Mac build 71, upstream PR #303)

1. **Server reply.** `AuthenticatePairedDeviceClient` sends its own `Authentication` on the session after verification. It is awaited and sent before `ConnectionStatusChanged`, so it always precedes `DeviceInfo` on the wire. Its existing session-replacement check now skips the session being authenticated.
2. **Legacy server acceptance on the client.** `ConnectCore` records the pinned client in `awaitingServerAuthentication` before sending `Authentication`. If the first message on that client is not `Authentication`, the server is an official ≤3.2.0 desktop that accepted us silently. Such a server sends application messages (`DeviceInfo` first) only after marking us connected. The TLS session was pinned to the stored certificate with `SslHelper.CreateSslContext(device.Certificate)`. The client then runs `AuthenticatePairedDeviceServer` and routes the deferred message afterward. A rejecting server closes the connection and sends nothing, so it is not accepted. The trust level matches the existing path, which accepts any `Authentication` from that pinned server.
3. **Idempotent client acceptance.** `AuthenticatePairedDeviceServer` returns early when the same client is already connected. Otherwise a late `Authentication` after legacy acceptance would run `DisconnectClient(pairedDevice.Client)` on the very connection being accepted. That would leave a Connected device with a disposed client.
4. **One reply per connection burst.** `ReplyAuthentication` sends the reply only if the session is still the device's current one, and not if another session of the device was answered in the last 3 s (`DuplicateConnectionWindow`). The decision is made under a lock after the awaited message creation, not before it. Right after startup, `GetLocalDeviceAsync` can take longer than the 59 ms gap between two connections. A check made before the await missed the first reply and answered both.
   Why: a ≤3.2.0 client that connects twice and gets a reply on both closes its own newest connection (`AuthenticatePairedDeviceServer`: `IsConnected && Client != null` → `DisconnectClient(Client)`). It then stays Connected on a disposed client and never reconnects. This happened to ROG with build 70 on 2026-10-17, and Mac→ROG clipboard stopped. Without a second reply, the old client keeps the client that `ConnectCore` assigned last, which is the session the server kept.
5. **Per-line framing.** `GetMessagesFromBuffer` catches deserialization failures per line and logs a warning. A newer peer's unknown message type no longer discards the rest of the batch, including an `Authentication` in the same read.

## What a Mac-only patch means with Store 3.2.0 Windows peers

| Who connects | Before | Mac build 71 + Store Windows |
|---|---|---|
| Windows → Mac (Windows is client) | Mac connected; Windows saw Mac Disconnected; only Mac→Windows worked | Mac replies; Windows runs its existing `HandleServerAuthentication` and marks Mac connected. Both directions work. |
| Mac → Windows (Mac is client) | Windows connected; Mac saw Windows Disconnected; only Windows→Mac worked | Windows still sends no reply, but its `DeviceInfo` triggers legacy acceptance on the Mac. Both directions work. |
| Windows ↔ Windows (OMEN↔ROG) | One-way | Unchanged; both run Store builds. |

The direction no longer depends on who connects when the Mac is one end.

## Cross-connects and loops

Connection attempts come only from discovery events (UDP broadcast, mDNS answers, UI Connect) and are skipped while the device is connected or connecting. A disconnect never triggers an immediate reconnect, so there is no tight loop. Once both ends mark the link connected, discovery stops creating connections. Before this fix the client side never got there.

The 3.2.0.10 fork serializes registration and outgoing-client installation with a per-peer gate. If opposite-direction links overlap, the link initiated by the ordinally smaller device ID wins. A lone inbound or outbound link remains usable. This removes the simultaneous-connect race while preserving ordinary stale-link replacement. The network regression now exercises the production NetworkService with both sides dialing at once and bidirectional Unicode messages.

Item 4 contains the Store-build double-connect hazard. One rare ordering remains: the reply on the first connection is processed only after the client assigned its second connection, and the server then closes the first. The old client ends Disconnected with an orphaned live connection. It recovers on the next discovery event and never ends in a Connected-but-dead state.

## Upstream proposal

On 2026-10-17 the maintainer accepted the diagnosis and the server reply but asked for no compatibility code for older versions. https://github.com/shrimqy/Sefirah/pull/303 (branch `fix/desktop-auth-reply`, one commit 42e2ad6 on v3.2.0, +9 lines) now contains only item 1's reply and item 3's early-return guard. The guard is needed even when both ends are patched, because a double-connecting client gets one reply per connection. Items 2 and 4 (the ≤3.2.0 compatibility code) and item 5 (#281) stay in the fork only, as long as the Windows peers run Store 3.2.0. Once an upstream release with #303 is on every peer, drop items 2 and 4 from the fork and follow upstream.

## Verification

- Baseline on build 69 (2026-10-16): a probe on ROG with ROG's own PFX sent an unknown-type line plus `Authentication` in one write. The Mac logged a `DeserializeMessage` exception in `GetMessagesFromBuffer` and sent nothing back.
- Build 71, 2026-10-17. It ran temporarily from /tmp staging against the real LocalState, then build 69 was restored. Identity, PFX, peer certificates and peer address lists were unchanged, and the Mac clipboard was restored.
  - Three Mac restarts. OMEN and ROG each connected twice (59–151 ms apart) and got exactly one reply. Windows logged `Paired device Meta-MacOS connected successfully`. Clipboard Mac→ROG, Mac→OMEN, ROG→Mac and OMEN→Mac all arrived each round.
  - The ROG probe (`client`) passed: it got the unknown line skipped, then `Authentication` before `DeviceInfo`.
  - Mac as client of the real Store ROG server passed: `accepted this client without an Authentication reply`, with clipboard in both directions.
- Test-environment note: the build run from /tmp did not receive LAN unicast UDP on 5149, though plain Python on the Mac did. This is probably macOS Local Network privacy, which is keyed per bundle/executable. The probe `server` mode therefore failed to trigger a connection. The Mac-as-client run instead used a truthful mDNS answer multicast from the Mac (ROG's real ID, 192.168.31.32:5150), because that path takes the address from the A record. Scripts are in /tmp/.agents/sefirah-desktop-auth-20261016 and the session scratchpad.
## Desktop link recovery (3.2.0.3, 2026-11-11)

Observed on 2026-11-11 with every desktop on fork 3.2.0.2: Mac↔OMEN and Mac↔ROG stayed down for hours while ROG↔OMEN worked, so clipboard between the Mac and the Windows machines silently stopped.

1. **UI-thread writes during authentication.** `AuthenticatePairedDeviceServer` and `AuthenticatePairedDeviceClient` set `pairedDevice.Address` off the UI thread. `Address` is bound in the UI; when it changes (for example LAN ↔ Tailscale address), WinUI throws `COMException 0x8001010E` (RPC_E_WRONG_THREAD) from `PropertyChanged`. The client then logged `Error in client authentication` and closed the link. The field was already updated, so a second attempt raised no change event and succeeded, which is why it looked random. Uno on macOS does not throw. Rule: every observable `PairedDevice` property is written inside `DispatcherQueue.EnqueueAsync`.
2. **No recovery between desktops.** Discovery broadcasts once at startup and mDNS answers rarely repeat; phones reconnect on their own, desktops never did. After sleep or a network change the link stayed down until one app restarted. `NetworkService` now runs a reconnect loop for computer peers (GUID device IDs): every 20 s for the side whose device ID is ordinally smaller, every 4th tick for the other side, exponential backoff up to 5 min per unreachable peer, reset on `NetworkAddressChanged` and on connect. The preferred-initiator rule keeps both sides from dialing at once and closing each other's link (the cross-connect race above). The last connected address is dialed first.
3. **Half-open links.** Server sessions and client connections enable TCP keepalive (30 s idle, 10 s interval, 3 probes), so a peer that slept is detected in about a minute and the loop can redial.
4. **No ADB for computers.** `ConnectionStatusChanged` ran `adb connect <desktop>:5555` for desktop peers and, after it failed, `adb tcpip 5555` on the only USB device (an emulator), restarting its adbd every minute. ADB auto-connect and screen mirroring now apply to mobile peers only, `IsMatchingAdbDevice` is false for computers, and `TryConnectTcp` never falls back to an unrelated USB device.

## macOS discovery via Bonjour

On macOS `IMdnsService` is `MacBonjourService`, which calls dns_sd from libSystem: DNSServiceRegister for the advertisement, then DNSServiceBrowse, Resolve and IPv4 GetAddrInfo for discovery. Every DNSServiceRef stays on one serial dispatch queue (operations are posted with dispatch_async_f), and the browse restarts every 2 minutes so cached instances are offered again after a peer restarts without sending a goodbye. Windows keeps MeaMod. TXT keys and the advertised ports match MeaMod (SRV = discovery port, `serverPort` = TCP server).

Reason: the MeaMod MulticastService started at login, before Wi-Fi was ready, never recovered. The Mac's own `dns-sd -B` did not list the Mac, the Mac never dialed peers discovered over mDNS, and phones could not find it after a reboot. The same MeaMod version worked from a terminal and after a manual relaunch, so this was a startup-timing failure, not Local Network permission. Info.plist declares NSLocalNetworkUsageDescription and NSBonjourServices `_sefirah._udp`.

## Transport reliability in 3.2.0.10

See ../device-protocol.md for TLS connection binding, ordered dispatch, framing, disconnect, timeouts and compatibility fixtures. Outgoing connections now stay Connecting until authentication succeeds or times out. Windows and macOS share the same framing and transfer components.
