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

## Verification

- `Sefirah.BluetoothCatalog.Regression` covers grouping policy, legacy endpoint support, endpoint
  exclusion, and catalog identity behavior.
- Physical checks may read catalogs without switching a headset. Actual switch testing must
  preserve the intended final radio and endpoint state.
