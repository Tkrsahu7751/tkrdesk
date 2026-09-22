# Apna Remote

Apna Remote is an attended remote-desktop lab for devices on the same Wi-Fi/LAN. The Windows app can share or view a Windows screen; the Android app is a viewer. Every session requires host approval, and mouse/keyboard control stays off until the host allows it.

## Start here

- AI or developer: read `AGENTS.md`, then `STATE.json`, then `CONTINUE-HERE.md`.
- User testing two PCs: read `docs/USER-GUIDE.md`.
- Current build paths, hashes, blockers and next actions: read `STATE.json`.

## Repository map

| Path | Owns |
| --- | --- |
| `AGENTS.md` | Rules and startup/update protocol for every AI |
| `STATE.json` | Current version, status, artifacts, blockers and next actions |
| `CONTINUE-HERE.md` | One-screen human handoff |
| `WORKLOG.md` | Concise chronological work history |
| `docs/` | Architecture, decisions, procedures, release and privacy documents |
| `native/` | All maintained source code and developer scripts |
| `scripts/` | Repository maintenance checks |
| `evidence/` | Reviewed physical test evidence; no secrets or screen content |
| `dist/` | Latest local Windows and Android deliverables only |
| `.toolchain/`, `.nuget-packages/`, `.dotnet-home/` | Large local dependencies required for offline builds on this PC |

## Source layout

| Path | Responsibility |
| --- | --- |
| `native/ApnaRemote.Core` | Session permission, framing, input validation and pointer mapping |
| `native/ApnaRemote.Protocol` | Shared ARL1/TLS protocol for Windows and mobile viewers |
| `native/ApnaRemote.Windows` | WPF host/viewer, capture, LAN discovery and Windows input |
| `native/ApnaRemote.Viewer` | .NET MAUI Android/mobile viewer |
| `native/ApnaRemote.Core.Checks` | Dependency-free behavioral checks |

## Documentation map

| Need | Read |
| --- | --- |
| System structure and boundaries | `docs/ARCHITECTURE.md` |
| Why important choices were made | `docs/DECISIONS.md` |
| What should happen next | `docs/ROADMAP.md` |
| Build, automated, physical and soak tests | `docs/TESTING.md` |
| Two-PC usage and troubleshooting | `docs/USER-GUIDE.md` |
| Signing, packaging and Store gates | `docs/RELEASE.md` |
| Data and permission behavior | `docs/PRIVACY.md` |
| How to keep project memory current | `docs/PROJECT-MAINTENANCE.md` |
| Native build details | `native/README.md` |

The current build is a LAN lab. It has no internet relay, unattended service or cloud account. JPEG is supported; network H.264 is disabled.