# Two-PC LAN guide

Use the exact current Windows build listed in `../STATE.json` on both PCs. Apna Remote is an attended same-Wi-Fi/LAN lab.

## Connect

1. Put both PCs on the same private Wi-Fi/LAN.
2. On the host, open `ApnaRemote.exe` and select **Start sharing**.
3. Note the address, six-digit PIN and fingerprint.
4. On the viewer, enter all three values and select **Connect**.
5. Compare the fingerprint exactly. Stop if it differs.
6. On the host, select a screen and approve sharing.
7. Leave **Allow mouse and keyboard** off for view-only. Enable it only when intended.

The host can **Revoke control**, **Pause control** or **Stop sharing**. The viewer can select **End session**.

## If connection fails

- Confirm both PCs use the same artifact and network.
- Recheck the address, PIN and fingerprint.
- Make sure the host is still listening.
- `Allow-Firewall.bat` is optional and must be run deliberately as Administrator.
- Nearby discovery is a convenience; manual address entry remains available.
- Guest Wi-Fi isolation, VPNs and multiple adapters can block the connection.

## Safety and current limits

- The app does not change firewall rules automatically.
- PIN and fingerprint are not saved in preferences.
- JPEG is supported; H.264 is disabled.
- Lock/UAC/secure desktop cannot be remotely controlled.
- Read `TESTING.md` before treating any build as a release candidate.
- Release readiness and blockers are recorded in `../STATE.json`.