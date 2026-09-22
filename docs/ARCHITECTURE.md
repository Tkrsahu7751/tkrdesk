# Architecture

This document explains the stable system shape. Current versions, artifacts and blockers live only in `../STATE.json`.

## Product boundary

Apna Remote is an attended same-LAN remote desktop. A Windows app can host or view; the Android app views. There is one active attended session. Internet relay, unattended service, cloud accounts, stealth operation and UAC bypass are outside the current product.

## Components

| Component | Responsibility |
| --- | --- |
| `native/ApnaRemote.Core` | Pure session gate, framing, input validation and coordinate mapping |
| `native/ApnaRemote.Protocol` | Shared TLS/ARL1 viewer contracts |
| `native/ApnaRemote.Windows` | WPF UI, LAN host/viewer, capture, discovery, hardware hub (queue, pen drive, USB) and Windows input |
| `native/ApnaRemote.Viewer` | MAUI Android/mobile viewer |
| `native/ApnaRemote.Core.Checks` | Deterministic checks without real screen capture or OS input |

## Session flow

1. The host explicitly starts receiving and creates an ephemeral certificate and PIN challenge.
2. The viewer connects over TLS and verifies the displayed fingerprint.
3. The certificate-bound PIN proof succeeds before the host sees an approval request.
4. The host selects a display and approves view-only or optional control.
5. Capture and bounded JPEG delivery start. LIVE appears only after the first approved valid frame.
6. Revoke, pause, stop, timeout or disconnect cancels owned work and releases held input.

## Boundaries that must remain true

- UI state is owned by the UI thread; network, capture and decode work remain cancellable and bounded.
- Each connection attempt owns its socket, tasks, cancellation token and buffers. An old attempt cannot update a new session.
- One newest frame may wait while one encode/decode is active; superseded work is dropped.
- Input is checked for session state, permission epoch, sequence, size and selected-monitor mapping before OS effects.
- Discovery may advertise display name, IP and port. It never advertises PIN, fingerprint or trust.
- Lock, secure desktop, capture loss and target-monitor removal fail closed.

## Local-only project dependencies

`.toolchain/`, `.nuget-packages/` and `.dotnet-home/` support repeatable offline development on this machine. They are not source modules or release contents.