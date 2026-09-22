# Roadmap

The exact current milestone, blockers and next actions live in `../STATE.json`. This file defines gate order.

| Gate | Evidence needed before moving on |
| --- | --- |
| A — LAN behavior | Automated checks, both-direction two-PC matrix and 30-minute soak |
| B — Installation | Versioned reproducible package and clean-PC install/uninstall |
| C — Trust | Store identity/signing or OV certificate, public privacy URL and honest listing |
| D — Pairing review | Owner decision to keep mandatory fingerprint comparison or adopt an audited channel-bound PAKE |
| E — Release candidate | Signed candidate, full regression matrix and recorded evidence |

## Ordered work

1. Complete Gate A for the current Windows build.
2. Fix only reproduced lifecycle, mapping, discovery or performance failures.
3. Complete installation and signing gates.
4. Review the remaining pairing threat and make the PAKE decision.
5. Improve LAN onboarding only where physical tests show friction.
6. Add clipboard or file transfer later with separate consent and payload limits.
7. Revisit H.264 only with a maintained compatible stack, license/security review and soak evidence.

An internet edition requires authenticated signaling, STUN/TURN, efficient media, accounts, abuse prevention and a separate threat model. Do not expose the JPEG/TCP lab as an internet remote product.