# Apna Remote — current checkpoint

Updated: 2026-09-13

## Read this first

`STATE.json` is the authoritative current record. The repository is on branch `cursor/h264-network-stream` at baseline HEAD `7ba35ee600cb9e2cc57dd92a21c0f16c3e20d7fe`, with existing uncommitted work that must be preserved.

## Where the project stands

The latest local deliverables are Windows `0.20.6-lab` and Android viewer `0.17.0`. The supported path is attended same-LAN TLS with JPEG. Host approval and fingerprint comparison are mandatory; control is optional and host-granted. H.264, internet relay, unattended mode and public release are not supported.

Automated build and lifecycle checks for `0.20.6-lab` are recorded as green in `STATE.json`. They do not prove physical Wi-Fi, capture, input feel, lock/UAC behavior or clean-machine packaging.

## Next safe action

Run `docs/TESTING.md` on two physical PCs in both directions, then complete the 30-minute soak. Fix only failures that can be reproduced. Do not start internet/TURN work before these LAN gates pass.

For commands read `native/README.md`; for the reason and release order read `docs/ROADMAP.md` and `docs/DECISIONS.md`.