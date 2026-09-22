# Release and Microsoft Store preparation

Apna Remote is currently a lab. Exact readiness is recorded in `../STATE.json`.

## Required gates

- Current two-PC matrix and 30-minute soak pass using `TESTING.md`.
- Help and `PRIVACY.md` match actual behavior.
- The package is reproducible and passes clean-PC install, launch and uninstall.
- The owner chooses Microsoft Store identity/signing or a paid OV certificate.
- Public copy does not claim internet, unattended, H.264, full PAKE or enterprise readiness.

## Distribution choices

| Path | Result |
| --- | --- |
| Microsoft Store | Partner Center identity; Microsoft signs an accepted MSIX |
| Own website | Paid OV code-signing certificate reduces SmartScreen warnings |
| Private lab | Unsigned portable build clearly labelled as a lab |

Store certification is not guaranteed for a remote-control app. Keep claims narrow and explain attended consent.

## Store checklist

1. Complete the current test gates.
2. Verify the Partner Center developer account.
3. Reserve **Apna Remote** and copy the exact Package Identity Name and Publisher.
4. Replace temporary values in `native/store-package/AppxManifest.xml`.
5. Publish `PRIVACY.md` at a stable HTTPS URL.
6. Build with `native/Publish-StoreMsix.ps1`.
7. Confirm the package is JPEG-only and contains no unsupported codec binary.
8. Test install, launch, Share/Connect/End and uninstall on a clean PC.
9. Capture real Overview, consent, view-only and control/revoke screenshots.
10. Upload with honest capability explanations and release notes.

Do not invent Partner Center identity or submit temporary `CN=Apna Remote Lab` values.

## Listing baseline

**Short description:** Attended same-Wi-Fi remote desktop for Windows. View an approved PC, with optional mouse and keyboard control and one-click revoke.

**Behavior:** Apna Remote connects devices on the same private network over TLS. The host approves every session and selects the display. Control is off by default. JPEG is supported. There is no internet relay, unattended service or cloud account.

## Package checks

- Manifest identity/version match Partner Center.
- Privacy HTTPS URL works publicly.
- Automated and physical evidence names the exact artifact hash.
- Package has no PFX/P12 secret or unsupported codec binary.
- UI never shows LIVE before an approved frame.
- Listing says attended LAN only.