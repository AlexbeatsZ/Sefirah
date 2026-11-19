# Wireless ADB setup

## Incident and scope

Desktop 3.2.0.10 could repeatedly restart a reflashed Android device's adbd. Missing app identity fell back to model matching and inherited an old pairing ID. USB setup then preferred the old saved address. Each connection failure issued `adb tcpip 5555`, producing another offline/online event and concurrent setup attempts.

Version 3.2.0.12 shares the fix across Windows and macOS in `AdbService`, `WirelessAdbSetup` and `AdbDeviceIdentity`.

## Device association

- `AdbDevice.AndroidId` contains the identity read from the Android app's `device_info.txt`, or resolved by matching the USB device's live Wi-Fi IP to exactly one currently authenticated mobile peer. Scoped-storage or locked devices can make the file inaccessible even while the app is connected. Saved inactive addresses never qualify for this fallback.
- Network authentication can finish after USB discovery. `TryConnectTcp` refreshes unidentified online USB transports before choosing its target, so normal app startup restores automation without a manual replug.
- USB association requires this verified ID to equal the paired app's ID. Device models are display metadata and cannot establish an app identity after flashing or distinguish two identical models.
- Legacy Wi-Fi transports without the app identity can still associate through a saved endpoint. An explicitly different app ID takes precedence over the IP match.
- Unknown USB devices remain available in the explicit screen-mirroring selection dialog. Automatic wireless setup resumes after the actual app identity is paired and verified through USB or the live authenticated endpoint.

## Setup ownership and current device state

`TryConnectTcp` receives the paired device ID and returns the endpoint that actually connected. All network and screen-mirroring callers use this result. A verified USB device's current Wi-Fi address can differ from the pairing's saved address.

`WirelessAdbSetup` owns one in-flight operation per USB serial. Its lock survives disconnect events because enabling TCP mode itself causes those events.

1. Read the current IPv4 address from the USB device's `wlan0`. Skip setup while Wi-Fi has no usable address.
2. Read `service.adb.tcp.port`, then `persist.adb.tcp.port` when the service property has no valid port. Reuse the existing positive port, including a non-default port.
3. Attempt the current endpoint. If TCP mode is already enabled, a failed connection leaves adbd running for a later reconnect.
4. When TCP mode is disabled, record the enable attempt before executing the mode command. Attempts for the same serial and boot ID are spaced at least one minute apart. A real reboot permits a fresh attempt.
5. Enable port 5555 once, then retry the connection three times with bounded delays. Concurrent and restart-generated device events cannot issue another mode command.

Shell reads and the mode subprocess have eight-second timeouts. ADB connect has a ten-second timeout. The subprocess uses `ArgumentList` and drains both output streams while waiting.

The existing scrcpy implementation follows the same device-state approach by reading the live device IP and `service.adb.tcp.port` before deciding whether to enable TCP mode. Its `app/src/server.c` functions `get_adb_tcp_port` and `sc_server_switch_to_tcpip` were inspected on 2026-11-17. Source is https://github.com/Genymobile/scrcpy/blob/master/app/src/server.c.

## Device list parsing and tracking

AdvancedSharpAdbClient's `DeviceData` regex (3.5.15 through 3.7.17) rejects serials with spaces or parentheses. adb names duplicate mDNS service instances like `adb-xxxx (2)._adb-tls-connect._tcp`. The library's lazy `GetDevicesAsync` then throws mid-enumeration, and its `DeviceMonitor` loop exits on that ArgumentException, so a single such entry stops all ADB device tracking.

`AdbService` therefore never calls `AdbClient.GetDevicesAsync` or the library `DeviceMonitor`:

- `AdbDeviceList` parses `host:devices-l` / `host:track-devices-l`. The serial is the shortest prefix followed by a known state token. Unknown lines are reported and skipped, never thrown.
- `AdbDeviceTracker` reads `host:track-devices-l`, diffs snapshots by serial and raises the same connect/disconnect/change events. It is seeded with the online devices already added by the initial refresh. Socket failures wait, ensure the adb server is running and reconnect. Handler exceptions are logged per event.

Do not reintroduce library device list parsing for the device list or monitor without re-running the spaced-serial regression checks.

## Regression checks

Run `dotnet run --project tests/Sefirah.Adb.Regression -c Release`. The executable compiles the production identity and setup implementations directly and covers missing/new identities, legacy wireless association, stale addresses, absent Wi-Fi, existing and persistent TCP ports, concurrent events, restart reentrancy, failed enable cooldown, reboot recovery, USB exceptions and bounded connection retries.

After deployment, enable the affected device's ADB auto-connect, rediscover its USB transport and observe the app logs and adbd PID. A reflashed device without a paired app identity must log the identity skip. An already-enabled listener must never be restarted merely because its network connection fails.
