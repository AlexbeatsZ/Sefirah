# Sefirah Windows Fork Instructions

## Repository and Branch Policy

- This is AlexbeatsZ's Windows fork. Keep `origin` pointed at `AlexbeatsZ/Sefirah` and `upstream` at `shrimqy/Sefirah`.
- Keep changes reviewable for upstream where practical, but preserve fork-specific packaging, control API, and device-coordination behavior when required.
- Do not overwrite or discard unrelated dirty work. Commit and push only the files in the current task.

## Product Invariants

- Preserve user data, pairings, certificates, settings, and Cloud Files state across upgrades. Schema changes require explicit migrations and regression coverage.
- Bluetooth devices are identified by normalized address. Merge Classic and BLE records; classify devices by capability/category rather than name substrings.
- The catalog defaults to all paired devices; headset-only is an optional filter.
- The selected endpoint drives the Bluetooth UI and coordinator. Keep UI state, CLI state, and protocol state consistent.
- An accepted connect/disconnect request is not final success. Wait for observable state with bounded timeouts. If Android per-device connect fails, perform the defined target-radio recovery before waiting again.
- Serialize cross-peer handoff and use deterministic connection direction plus bounded retry/backoff to avoid simultaneous connection storms.
- Control-pipe operations are current-user-only and explicitly allowlisted. Never add arbitrary shell execution.
- Protocol changes must be capability-gated so older Android clients remain usable.

## Remote Storage and Security

- Hold the real lifetime task for every async provider, watcher, and queue. Do not wrap `async` delegates with `new Task` or unwrapped `StartNew`.
- Replacement of a sync root must await the old generation, serialize per root, and remove state only when the completing object is still the registered generation.
- Refresh stored SFTP context when credentials change and restart the provider safely.
- Orphan cleanup and device-rename reconciliation must never delete hydrated or unrelated local files.
- Treat Android remote storage as untrusted until it authenticates the paired desktop key and confines every operation to canonical selected-share paths.

## Packaging and Deployment

- Use the repository's reproducible signed x64 package/installer flow. Keep signing materials out of Git.
- Before any upgrade, verify the installed package identity and back up the relevant LocalState under `%LOCALAPPDATA%\Temp\.agents\` when rollback risk warrants it.
- After deployment, verify package status, preserved data, and the changed behavior. Do not rely on an old version number, hash, PID, endpoint, or radio state recorded in documentation.

## Verification

Run the smallest relevant regression suite first, then the complete affected Windows build/tests. Bluetooth changes must cover catalog identity/filtering and coordinator recovery. Cloud Files changes must cover provider replacement, cancellation, reconnect, and safe root reconciliation.

## Design References

- [`docs/design/bluetooth-handoff-ui.md`](docs/design/bluetooth-handoff-ui.md): Bluetooth catalog grouping, labels, endpoint choices, legacy compatibility, and action semantics. Read it before changing the handoff page or view model.

## Active Work

- Add safe orphan-sync-root recovery and device-rename reconciliation.
- Add selected remote shares and a unified shortcut hub while preserving on-demand hydration.
- Extend future non-Bluetooth controls only through an explicit allowlist and capability checks.
- Add automated multi-peer/headset coverage and repeat physical validation when devices are reachable.

## Current State

The feature branch contains the fork's Bluetooth catalog/handoff, control API, signed packaging, and Cloud Files provider work. Installed versions and physical-device state are volatile; recheck them before deployment or hardware validation. Use Git history and tests for completed implementation evidence rather than adding completed task boards here.

## Durable Lessons

- NetCoreServer `SendAsync` means that a complete frame was accepted into its locked internal
  buffer, not that the network is empty. Waiting for global drain after every application frame
  can starve heartbeats. Keep application traffic in a bounded single-writer queue, let small
  control frames enter the transport buffer directly, and configure a transport buffer limit.
