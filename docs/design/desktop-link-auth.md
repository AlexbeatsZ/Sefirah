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

Connection replacement is still newest-wins on each side, as upstream: server-side authentication closes our client to that peer, and client-side authentication closes our session from it. If both desktops connect at once (conn1 A→B and conn2 B→A), each side can close the other link and leave none. Both then wait for the next discovery event. This race existed upstream without the reply. A deterministic tie-break, such as keeping the link whose client has the smaller device ID, would need both ends patched. It would also keep a half-open stale link after a network change, which newest-wins handles. It is therefore not used.

Item 4 contains the Store-build double-connect hazard. One rare ordering remains: the reply on the first connection is processed only after the client assigned its second connection, and the server then closes the first. The old client ends Disconnected with an orphaned live connection. It recovers on the next discovery event and never ends in a Connected-but-dead state.

## Upstream proposal

Ready for review: https://github.com/shrimqy/Sefirah/pull/303 (branch `fix/desktop-auth-reply`, one commit on v3.2.0) with items 1–4. Item 5 duplicates upstream PR #281 and is left out. #303 merges cleanly with #281 and #280. Android needs no change.

## Verification

- Baseline on build 69 (2026-10-16): a probe on ROG with ROG's own PFX sent an unknown-type line plus `Authentication` in one write. The Mac logged a `DeserializeMessage` exception in `GetMessagesFromBuffer` and sent nothing back.
- Build 71, 2026-10-17. It ran temporarily from /tmp staging against the real LocalState, then build 69 was restored. Identity, PFX, peer certificates and peer address lists were unchanged, and the Mac clipboard was restored.
  - Three Mac restarts. OMEN and ROG each connected twice (59–151 ms apart) and got exactly one reply. Windows logged `Paired device Meta-MacOS connected successfully`. Clipboard Mac→ROG, Mac→OMEN, ROG→Mac and OMEN→Mac all arrived each round.
  - The ROG probe (`client`) passed: it got the unknown line skipped, then `Authentication` before `DeviceInfo`.
  - Mac as client of the real Store ROG server passed: `accepted this client without an Authentication reply`, with clipboard in both directions.
- Test-environment note: the build run from /tmp did not receive LAN unicast UDP on 5149, though plain Python on the Mac did. This is probably macOS Local Network privacy, which is keyed per bundle/executable. The probe `server` mode therefore failed to trigger a connection. The Mac-as-client run instead used a truthful mDNS answer multicast from the Mac (ROG's real ID, 192.168.31.32:5150), because that path takes the address from the A record. Scripts are in /tmp/.agents/sefirah-desktop-auth-20261016 and the session scratchpad.