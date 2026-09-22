# Apna Remote — instructions for AI agents

This repository is the complete working home for Apna Remote. Start at this file whenever an AI receives only the project path.

## Mandatory startup order

1. Read `STATE.json` for current machine-readable truth.
2. Read `CONTINUE-HERE.md` for the short human checkpoint.
3. Run read-only Git checks: status, branch, HEAD and tag.
4. Read only the document relevant to the task from the map in `README.md`.
5. When working under `native/`, also read `native/AGENTS.md`.

If chat history conflicts with the repository, stop and report the conflict. Do not silently choose an older statement.

## Safety and ownership

- Work read-only first. Preserve all existing uncommitted changes.
- Never reset, clean, checkout over, discard or overwrite another person's work.
- Do not edit source, create/delete/move files, build, install, publish, deploy, change firewall settings, commit or push unless the owner has authorized that action.
- Never open, print, edit, copy or commit `.env`, credentials, certificates, PFX/P12 files, tokens or private keys.
- Treat `.toolchain/`, `.nuget-packages/` and `.dotnet-home/` as local development dependencies. Do not delete or reinstall them casually.
- Treat `dist/` as local deliverables and `evidence/` as test evidence. Replace them only as part of an approved release/test task.
- Never change protocol, authentication, consent, capture/input boundaries, packaging identity or architecture without explicit owner approval.

## Product invariants

- The current product is attended, same-LAN remote viewing with optional host-approved control.
- Receiving is opt-in. View-only is the default.
- Never show LIVE before an approved valid frame arrives.
- Revoke, pause, stop, disconnect and timeout must release held input immediately.
- Never bypass TLS/fingerprint validation, host approval or Windows secure-desktop boundaries.
- Do not claim internet, unattended, H.264, Store-ready or production-ready support until the recorded gates pass.

## Change workflow

For code work: inspect, propose a small plan, list exact files, obtain owner approval, make the scoped change and validate it. Do not commit without separate approval.

At the end of every approved change:

1. Update `STATE.json` if version, milestone, artifact, blocker, evidence or next action changed.
2. Update `CONTINUE-HERE.md` only when the current checkpoint changed.
3. Add one concise entry at the top of `WORKLOG.md`.
4. Update `docs/DECISIONS.md` only for a durable decision and `docs/ARCHITECTURE.md` only for a structural change.
5. Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Project.ps1`.
6. Run the task-specific checks in `docs/TESTING.md` when source behavior changed.

Do not duplicate status across many documents. `STATE.json` owns current facts; `WORKLOG.md` owns history; `docs/DECISIONS.md` owns the reasons behind durable choices.