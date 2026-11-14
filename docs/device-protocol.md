# Device transport and compatibility

## Ownership

Windows and macOS share `NetworkService`, TLS validation, UTF-8 framing and file-transfer handlers. Platform services provide native capabilities. Android owns transport sessions in `core/network`; the domain model exposes device data and repository contracts. `DeviceManager.updatePairedDevice(id, transform)` merges individual changes under the repository mutex after startup loading completes.

Each live connection processes application messages in wire order. Desktop sockets feed bounded per-connection queues. Android awaits each inbound handler. Local feature lifecycle uses a thread-safe set of consumers. Clipboard and remote controls retain the Android shell worker independently.

## JSON stream

Control connections carry UTF-8 JSON objects separated by LF. CRLF is accepted. Decode complete byte-framed lines so a multi-byte code point can cross arbitrary socket reads. Unsupported or malformed JSON messages are skipped individually. Desktop frames are limited to 16 MiB and a connection queue to 1024 messages.

`type` is the case-sensitive message discriminator. Enum names remain compatible with the 3.2.0 baseline, including `MediaAction.actionType = Stop`. `Authentication.protocolVersion` is optional and defaults to zero for legacy peers. Version 1 confirms the updated authentication lifecycle. New peers receive one reply per transport. Legacy peers retain the reply-per-burst workaround. Optional `DeviceInfo` extensions negotiate controls and presentation through their own `version` fields. Absent phone numbers preserve saved numbers; an explicit empty list clears them.

Desktop contract fixtures are in `protocol/messages.json`. The same fixtures ship in Android `domain/src/test/resources/protocol-messages.json`. Both production serializers must parse and round-trip every fixture. Copy fixture changes to both repositories and compare the files when releasing them together.

## Authentication and connection replacement

Authentication public keys must match the certificate presented by that TLS connection. Paired device certificates remain pinned byte-for-byte. An existing PFX that cannot be read causes an error. New identities are written atomically and never replace an existing file.

NetCoreServer 8.0.7 invokes `OnHandshaking` immediately before beginning `SslStream` authentication. `TlsHandshake` uses the captured ExecutionContext to bind certificate validation to that transport. The shared transport regression exercises 24 concurrent mutually authenticated connections. macOS .NET 10 SecureTransport can abort in SSLRead/SSLClose when NetCoreServer sends TLS close-notify while a read is pending. Close each transport once and dispose its captured SslStream before the macOS base disconnect. Application-level Disconnect is transmitted before teardown. Re-run this test when changing NetCoreServer or the runtime.

Desktop registration serializes changes per peer. If opposite-direction desktop links overlap, the link initiated by the ordinally smaller device ID wins. A single reachable direction remains usable. Outgoing peers remain Connecting until authentication succeeds or a ten-second deadline expires. Pinned legacy desktops can still confirm acceptance by sending their first application message.

User disconnects send `Disconnect` before closing transport and suppress retries. Received `Disconnect` applies the same local state without echoing another command. Service teardown on Android releases sockets, discovery and feature consumers. It preserves the distinction between service lifecycle and a manual disconnect.

## File transfer

New receivers send `start\n` and `complete\n`. Senders also accept legacy fixed ASCII controls without LF. Fragments and multiple controls in one socket read are supported. Readiness is buffered before the send loop starts. Data consists of exactly the advertised byte count for each file. Zero-byte files still exchange both controls.

Waiting for a receiver, a transfer control, or stalled file data is bounded to thirty seconds. TLS connection establishment is bounded to ten seconds. Cancelling a transfer releases its sockets even when coroutine cancellation is already active.

Remote names must be single path components. Reject path separators, drive prefixes, control characters, `.` and `..`. Desktop destinations use exclusive creation with numbered collision suffixes. Failure cleanup deletes only the new incomplete path. Existing destination files are preserved.

## Discovery

Advertise TCP service ports through both SRV and the `serverPort` TXT field. Read `serverPort` when present for compatibility with older desktop advertisements whose SRV port was UDP 5149. Android advertisements that omit the TXT field use their SRV port.

Windows caches TXT, SRV and IPv4 answers across packets with record expiry. macOS resolves through system Bonjour and expires pending native references after ten seconds. Android retains one callback per discovered service and replaces older endpoint metadata when updates arrive. Invalid UDP datagrams affect only that packet.

Desktop announcements recompute interface broadcasts and paired addresses periodically. Android announcements run only while discovery is active and obey the current trusted-network decision. Stop releases advertisements, multicast locks and UDP sockets.

## Inbound authorization

Android network service and boot receiver are private app components. System boot broadcasts still reach the manifest receiver. Headless management retains its DUMP and shell/root checks.

Android clipboard receive and image transfers check per-device sync settings. Legacy ringer, DND and volume commands check device-control consent. Desktop audio, media and image-clipboard commands check their feature settings. Configured desktop actions require the separate per-device `AllowRemoteActions` opt-in, which defaults to false for existing and new devices.

## Verification

Desktop transport regression links production network, socket, TLS, framing, serializers and transfer handlers. UI, storage adapters and notifications use isolated test hosts. It covers byte splits, shared JSON fixtures, concurrent TLS identities, actual zero-byte/bulk/collision transfers and simultaneous desktop connections. It leaves live app identities and paired-device data untouched.

Android unit regressions cover ordered callbacks, EOF, close from inside a callback, draining disconnect, legacy/new transfer controls, protocol fixtures, reconnection, clipboard worker sessions and existing control/presentation validation.

Physical Windows/macOS/Android interoperability, login with late Wi-Fi, sleep/wake and long locked-device survival require device acceptance in addition to these checks.
