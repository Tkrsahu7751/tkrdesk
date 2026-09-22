# Privacy policy draft

Last updated: 2026-09-13

This draft covers the attended same-LAN Windows app and Android viewer. Replace publisher/contact placeholders and host it at a stable HTTPS URL before Store submission.

## Summary

Apna Remote lets a user view and optionally control a computer after the host approves the session. The current build works on the same local network. It has no Apna Remote cloud account or relay, does not sell personal data and does not provide unattended access.

## Data handled

| Data | Purpose and storage |
| --- | --- |
| Theme, quality and recent addresses | Stored locally for convenience |
| Session history and performance metrics | Stored locally; may include time, peer label, result, frames and resource measurements |
| Ephemeral certificate/session keys | Used in memory to encrypt the LAN session |
| PIN | Shown on host and entered by viewer; not saved; raw PIN is not sent |
| Fingerprint | Compared by users to verify TLS identity; not uploaded |
| Screen pixels and optional input | Sent only to the approved peer during the active TLS session |

Metrics and logs must not contain PINs, typed text or screen content.

## Permissions

- **Private network:** connect/listen on TCP port 5720.
- **Screen capture:** starts after host approval and display selection.
- **Mouse/keyboard input:** only after the host allows control.
- **Camera/Bluetooth, where offered:** scan a QR or find a nearby LAN offer; discovery does not grant access.
- **Full trust on Windows:** required by the WPF capture/input implementation; the app runs as the signed-in user.

## Current exclusions

- No unattended or always-on service
- No automatic firewall change
- No internet/cloud relay
- No clipboard or file transfer
- No advertising ID or analytics SDK
- No saved PIN or silent trusted-device access

The optional `Allow-Firewall.bat` is separate, visible and requires Administrator consent.

## Consent, contact and changes

Use Apna Remote only when the person at the host understands and approves the session. The app is not directed at children under 13 and must not be used for hidden monitoring.

Use the support email registered with the Store publisher for privacy questions. Update the date and public Store copy whenever data handling, permissions, mobile features or network behavior changes.