# Testing and evidence

Current automated results and pending gates are recorded once in `../STATE.json`. This document owns procedures and evidence standards.

## Automated checks

From the repository root:

```powershell
& '.\native\Build-And-Check.ps1' -DotnetPath '.\.toolchain\dotnet\dotnet.exe'
& '.\.toolchain\dotnet\dotnet.exe' '.\native\ApnaRemote.Windows\bin\Release\net10.0-windows10.0.19041.0\ApnaRemote.dll' --discovery-lifecycle-selftest
& '.\.toolchain\dotnet\dotnet.exe' '.\native\ApnaRemote.Windows\bin\Release\net10.0-windows10.0.19041.0\ApnaRemote.dll' --host-lifecycle-selftest
& '.\.toolchain\dotnet\dotnet.exe' '.\native\ApnaRemote.Windows\bin\Release\net10.0-windows10.0.19041.0\ApnaRemote.dll' --viewer-lifecycle-selftest
git diff --check
```

These checks use loopback, synthetic peers and fake input sinks. They do not prove real Wi-Fi, capture, Bluetooth radio, SendInput feel or packaging on another PC.

## Same-PC smoke

1. Open the same Windows build twice.
2. Window A starts sharing; window B connects to `127.0.0.1`.
3. Share a different monitor when possible.
4. Test view-only, control, Revoke, End and Stop.

Mirror feedback and higher CPU are expected if the viewer is inside the captured screen. Do not record this as network/performance evidence.

## Required two-PC matrix

Use the exact Windows artifact declared in `../STATE.json` on both PCs and test both role directions.

| Area | Required checks |
| --- | --- |
| Setup | Same private LAN, matching version, host address/PIN/fingerprint visible |
| Pairing | Correct values connect; wrong fingerprint/PIN reject; Reject/Cancel are clear |
| View | Moving screen visible; first-frame state truthful; End/Stop clears image |
| Control | View-only blocks input; click, type, modifiers, drag and wheel work after grant |
| Consent | Revoke/Pause stop input; regrant needs host action and current epoch acknowledgement |
| Mapping | Same-size monitors, negative origin and mixed DPI target the selected monitor |
| Cleanup | Blur, Alt-Tab, End, Stop and disconnect leave no stuck inputs |
| Failure | Lock/UAC fails closed; Wi-Fi interruption and firewall errors are clear |
| Repetition | 20 connect/end cycles without stale state or resource growth |
| Packaging | Declared artifact runs on a clean second PC; install/uninstall is recorded |

Record PASS, FAIL or SKIP with date, exact artifact hash, OS, DPI and exact failure text. Fix only reproducible failures.

## Thirty-minute soak

1. Use the same declared build on both PCs.
2. Run moving and mostly static content for at least 30 minutes total.
3. Exercise view-only, control, pause/revoke and one reconnect.
4. Collect each PC's `%LocalAppData%\ApnaRemote\metrics` summary and CSV.
5. Save reviewed evidence under `../evidence/metrics` without PINs, typed text or screen content.

Review FPS, kbps, frames, drops, memory, hang/lag flags and timer skew. Do not claim a performance target without measurements.

## After testing

Update test status in `../STATE.json`, the checkpoint in `../CONTINUE-HERE.md` and one entry in `../WORKLOG.md`. Keep raw evidence in `evidence/`; do not paste it into several documents.