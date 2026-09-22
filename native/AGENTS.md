# Native source instructions

Root `../AGENTS.md` always applies. Read `../STATE.json`, `../CONTINUE-HERE.md`, `README.md`, `../docs/ARCHITECTURE.md` and the relevant part of `../docs/TESTING.md` before source work.

## Source boundaries

- `ApnaRemote.Core`: pure permission/session rules, framing, validation and coordinate math.
- `ApnaRemote.Protocol`: shared ARL1/TLS contracts; keep Windows and mobile compatible.
- `ApnaRemote.Windows`: WPF host/viewer, Windows capture, LAN discovery and SendInput.
- `ApnaRemote.Viewer`: MAUI mobile viewer.
- `ApnaRemote.Core.Checks`: deterministic behavioral checks.

Do not weaken TLS/fingerprint verification, certificate-bound PIN proof, five-failure limit, explicit host approval, view-only default, permission epochs, bounds, cancellation or held-input cleanup.

## Development rules

- Use the repository SDK path from `README.md`; do not alter system PATH or reinstall dependencies without approval.
- Keep network/capture/decode work bounded and cancellable. Old attempts must not update a new session.
- Maintain exact selected-monitor mapping. Picker/window capture stays view-only unless separately designed and approved.
- JPEG is the supported path. Do not restore H264Sharp/OpenH264 without a compatible ABI, license/security review and soak evidence.
- Builds recreate `bin/` and `obj/`; they are never source.
- For an approved behavior change, run the focused checks plus `Build-And-Check.ps1`, then update the root state/worklog contract.