# Bluetooth Handoff UI Contract

The Bluetooth page is a device catalog, not a view filtered to one selected peer.

## Grouping and labels

- Show every visible paired Bluetooth device by default. The headset-only filter is optional.
- Use exactly two sections: connected and disconnected. A non-null `ActiveEndpointId` is the
  connection truth; the currently selected peer does not change section membership.
- A connected row includes the endpoint name. A disconnected row shows only the Bluetooth device
  name.
- Identity and merging use normalized Bluetooth addresses. Display names are not identifiers.

## Actions

- `Switch to this device` targets the local Windows endpoint.
- `Switch to other device` lists every endpoint except the local endpoint and the current active
  endpoint. Unsupported explicit endpoints remain visible but disabled with an explanation.
- `Disconnect` is enabled only when an active endpoint is known and targets that endpoint.
- An empty supported-endpoint list is the backward-compatible legacy meaning of “all endpoints”.
- A request being accepted is not success. The coordinator must wait for the requested Bluetooth
  state with a bounded timeout and report the final result.
- Bystander suppression follows endpoint capability. On a per-device endpoint, disconnect only
  the headset when it is connected and leave the Bluetooth radio alone when it is already
  disconnected. Use temporary radio suppression only for endpoints that lack per-device control.
- An endpoint whose live catalog reports an unavailable controller remains visible for diagnosis,
  but its switch and disconnect actions are disabled. Keep the endpoint-specific failure visible
  across periodic refreshes so a dead Shizuku bridge is not mistaken for a successful no-op.

## macOS adapter

- `system_profiler SPBluetoothDataType -json` reports connection state through the
  `device_connected` and `device_not_connected` groups. Parse those group names as the source of
  truth; an individual device object does not reliably repeat a connected flag.
- Keep optional `blueutil` discovery separate from native `IOBluetooth` availability. A missing
  executable must fall through to the signed, bundled native helper instead of being invoked by
  name. Radio control may report a structured unavailable result when `blueutil` is absent.
- The app bundle must declare `NSBluetoothAlwaysUsageDescription`. macOS terminates the process at
  the TCC boundary before managed error handling runs when native `IOBluetooth` is accessed without
  that declaration.
- Sign the app bundle and every nested Mach-O with the persistent `Sefirah Local Code Signing`
  identity. Ad-hoc signatures use a changing code-directory hash as their designated requirement,
  so TCC can request Bluetooth permission again after each rebuild.
- Check `IOBluetoothDevice.isConnected` before calling `openConnection` or `closeConnection`.
  Requesting the already-satisfied transition can block indefinitely on current macOS releases;
  keep the native helper in a subprocess with a bounded timeout as an additional safety net.
- The periodically refreshed device rows must retain stable instances by headset identity. Reset
  the button visual state on pointer exit, initial load, and menu close so Uno/Skia cannot retain a
  recycled `PointerOver` state.

## Verification

- `Sefirah.BluetoothCatalog.Regression` covers grouping policy, legacy endpoint support, endpoint
  exclusion, catalog identity, macOS profiler groups, bystander suppression, and stable collection
  reconciliation.
- Physical checks may read catalogs without switching a headset. Actual switch testing must
  preserve the intended final radio and endpoint state.
