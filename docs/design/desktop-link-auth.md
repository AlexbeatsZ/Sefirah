# Two-way desktop link authentication

Read this before changing `NetworkService` authentication, connection replacement or message framing.

## Problem in official ≤3.2.0

A link has one TLS client and one server. The client sends `Authentication`. The server verifies the client certificate against the stored paired certificate (`HandleServerSessionAuthentication` → `AuthenticatePairedDeviceClient`) and marks the client connected. A desktop server never replied for a paired device; `SendAuthenticationMessage` ran only for discovered devices. The connecting desktop therefore never ran `HandleServerAuthentication`, so it never marked the server connected. `ConnectCore` sets it back to Disconnected right after the handshake. Clipboard and other broadcasts go only to `IsConnected` devices, so only the server-to-client direction worked.

Android servers reply with `Authentication` (`authenticatePairedDevice` in the Android `NetworkService`), so phone links work. Android as a client marks itself connected without waiting for a reply and ignores any later `Authentication` (`else -> {}` in its `handleMessage`).

The connecting desktop stayed Disconnected, so each later mDNS/UDP discovery event started a new client connection. The server then replaced its session. Mac logs from 2026-10-16 12:04:05 show OMEN and ROG each connecting twice within 350 ms.

## Fix (Mac build 70)

1. **Server reply.** `AuthenticatePairedDeviceClient` sends its own `Authentication` on the session after verification. It is awaited and sent before `ConnectionStatusChanged`, so it always precedes `DeviceInfo` on the wire. Its existing session-replacement check now skips the session being authenticated.
2. **Legacy server acceptance on the client.** `ConnectCore` records the pinned client in `awaitingServerAuthentication` before sending `Authentication`. If the first message on that client is not `Authentication`, the server is an official ≤3.2.0 desktop that accepted us silently. Such a server sends application messages (`DeviceInfo` first) only after marking us connected. The TLS session was pinned to the stored certificate with `SslHelper.CreateSslContext(device.Certificate)`. The client then runs `AuthenticatePairedDeviceServer` and routes the deferred message afterward. A rejecting server closes the connection and sends nothing, so it is not accepted. The trust level matches the existing path, which accepts any `Authentication` from that pinned server.
3. **Idempotent client acceptance.** `AuthenticatePairedDeviceServer` returns early when the same client is already connected. Otherwise a late `Authentication` after legacy acceptance would run `DisconnectClient(pairedDevice.Client)` on the very connection being accepted. That would leave a Connected device with a disposed client.
4. **Per-line framing.** `GetMessagesFromBuffer` catches deserialization failures per line and logs a warning. A newer peer's unknown message type no longer discards the rest of the batch, including an `Authentication` in the same read.

## What a Mac-only patch means with Store 3.2.0 Windows peers

| Who connects | Before | Mac build 70 + Store Windows |
|---|---|---|
| Windows → Mac (Windows is client) | Mac connected; Windows saw Mac Disconnected; only Mac→Windows worked | Mac replies; Windows runs its existing `HandleServerAuthentication` and marks Mac connected. Both directions work. |
| Mac → Windows (Mac is client) | Windows connected; Mac saw Windows Disconnected; only Windows→Mac worked | Windows still sends no reply, but its `DeviceInfo` triggers legacy acceptance on the Mac. Both directions work. |
| Windows ↔ Windows (OMEN↔ROG) | One-way | Unchanged; both run Store builds. |

The direction no longer depends on who connects when the Mac is one end.

## Cross-connects and loops

Connection attempts come only from discovery events (UDP broadcast, mDNS answers, UI Connect) and are skipped while the device is connected or connecting. A disconnect never triggers an immediate reconnect, so there is no tight loop. Once both ends mark the link connected, discovery stops creating connections. Before this fix the client side never got there.

Connection replacement is still newest-wins on each side, as upstream: server-side authentication closes our client to that peer, and client-side authentication closes our session from it. If both desktops connect at once (conn1 A→B and conn2 B→A), each side can close the other link and leave none. Both then wait for the next discovery event. This race existed upstream without the reply. A deterministic tie-break, such as keeping the link whose client has the smaller device ID, would need both ends patched. It would also keep a half-open stale link after a network change, which newest-wins handles. It is therefore not used.

There is one residual Store-build hazard. A Windows client can start a second `ConnectCore` in the short window between its handshake and the Mac's reply. Its unmodified `AuthenticatePairedDeviceServer` may then close its own newest client and stay Connected on a disposed connection. It recovers the next time the Mac connects to it, which legacy acceptance now completes. Build 70 has the guard from item 3 for its own client path.

## Upstream proposal

Submitted as draft https://github.com/shrimqy/Sefirah/pull/303 with items 1–3. Item 4 duplicates upstream PR #281 and is left out of #303. Items 1, 3 and 4 are protocol-compatible and fix the asymmetry once both ends run them. Item 2 is a mixed-version compatibility shim for servers without item 1. It is worth upstreaming while older desktop builds remain in use. Android needs no change.

## Verification

- Baseline on build 69 (2026-10-16): a probe on ROG with ROG's own PFX sent an unknown-type line plus `Authentication` in one write. The Mac logged a `DeserializeMessage` exception in `GetMessagesFromBuffer` and sent nothing back.
- Probe procedure for build 70 (scratch script, not in the repo; it ran on ROG with its own PFX):
  - `client` sends the same batch and expects `Authentication`, then `DeviceInfo`.
  - `server` nudges the Mac by unicast `UdpBroadcast` (port 5160), behaves like a legacy server, and expects the Mac's `DeviceInfo` with no second `Authentication`.
  - `nudge 5150` reconnects the Mac to the real ROG app, which exercises legacy acceptance against a real Store build.
