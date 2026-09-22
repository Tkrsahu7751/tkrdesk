# Native developer guide

Read `../AGENTS.md`, `../STATE.json` and `../CONTINUE-HERE.md` first. Current version and Git truth are intentionally stored only in `../STATE.json`.

## Solution layout

| Project | Responsibility |
| --- | --- |
| `ApnaRemote.Core` | Permission/session gate, framing, validation and pointer math |
| `ApnaRemote.Protocol` | Shared ARL1/TLS viewer protocol |
| `ApnaRemote.Windows` | WPF UI, LAN host/viewer, capture, discovery and SendInput |
| `ApnaRemote.Viewer` | MAUI Android/mobile viewer |
| `ApnaRemote.Core.Checks` | Deterministic behavioral checks |

## Build and checks

From the repository root, use the installed project SDK:

```powershell
& '.\native\Build-And-Check.ps1' -DotnetPath '.\.toolchain\dotnet\dotnet.exe'
```

Focused checks:

```powershell
& '.\.toolchain\dotnet\dotnet.exe' '.\native\ApnaRemote.Windows\bin\Release\net10.0-windows10.0.19041.0\ApnaRemote.dll' --discovery-lifecycle-selftest
& '.\.toolchain\dotnet\dotnet.exe' '.\native\ApnaRemote.Windows\bin\Release\net10.0-windows10.0.19041.0\ApnaRemote.dll' --host-lifecycle-selftest
& '.\.toolchain\dotnet\dotnet.exe' '.\native\ApnaRemote.Windows\bin\Release\net10.0-windows10.0.19041.0\ApnaRemote.dll' --viewer-lifecycle-selftest
```

Build output is disposable. Do not reinstall the SDK, change system PATH, add packages or publish without owner approval.

## Media and dependencies

Windows.Graphics.Capture is the capture path; JPEG is the supported network media. The old H264Sharp wrapper expected OpenH264 ABI 7, while patched OpenH264 2.6 uses ABI 8 and crashed that wrapper. Build/publish checks reject OpenH264/H264Sharp binaries. Do not restore this path without a compatible wrapper, license/security review and encode/decode/teardown soak evidence.

Future WebRTC work needs an exact revision, Windows ABI, codec/license/size review and a narrow native interface.

## Pairing

The host creates an ephemeral ECDSA P-256 certificate for each listen window. The viewer verifies its fingerprint. After TLS, a PBKDF2/HMAC proof binds the PIN to that certificate, and the host stops after repeated failures. Nearby advertisements contain name, IP and port only.

The short PIN is not a complete PAKE. Fingerprint comparison and host consent remain required. Never add accept-any-certificate validation or silent trust-on-first-use.

## Packaging

- `Publish-TwoPcLab.ps1`: portable JPEG-only Windows build, helper, checksum and packaged user guide.
- `Publish-StoreMsix.ps1`: Store/MSIX output after identity values are supplied.
- `Allow-Firewall.bat`: optional visible administrator action; never automatic.
- `Install-Lab.bat`: per-user lab copy/shortcut helper.

See `../docs/TESTING.md` for evidence and `../docs/RELEASE.md` for release gates.