# Project maintenance and AI memory

Chat history is temporary working memory. This repository is the durable project memory.

## One owner for each kind of information

| Information | Canonical file |
| --- | --- |
| Current version, artifact, blocker, milestone and next action | `../STATE.json` |
| Short handoff for a person | `../CONTINUE-HERE.md` |
| What changed and what was verified | `../WORKLOG.md` |
| Why a durable choice exists | `DECISIONS.md` |
| Components, flows and boundaries | `ARCHITECTURE.md` |
| Future gate order | `ROADMAP.md` |
| Test procedure and evidence format | `TESTING.md` |

Other documents should link to the owner instead of copying changing facts.

## AI recovery after compaction or a new session

1. Start at the repository root.
2. Read `AGENTS.md`, `STATE.json` and `CONTINUE-HERE.md`.
3. Verify Git status, branch, HEAD and tag before trusting the checkpoint.
4. Read only the task-specific document and the nearest nested `AGENTS.md`.
5. Inspect the actual files before changing them. Preserve uncommitted work.

This sequence gives the AI current truth without loading every historical document into its context window.

## End-of-task update matrix

| If this changed | Update |
| --- | --- |
| Version, build, artifact hash, milestone, blocker, next step or test result | `STATE.json`, then `CONTINUE-HERE.md` if the human checkpoint changed |
| Any completed work | One concise `WORKLOG.md` entry |
| Architecture or security boundary | `ARCHITECTURE.md` and a new `DECISIONS.md` row |
| User behavior | `USER-GUIDE.md`; also `PRIVACY.md` if data/permissions changed |
| Test method or release requirement | `TESTING.md` or `RELEASE.md` |
| Folder/file location | `README.md`, relevant `AGENTS.md` and checker |

## Clean-folder rules

- Keep only entry files and live state at root.
- Keep maintained source under `native/` and repository checks under `scripts/`.
- Keep long documents in `docs/`, organized by purpose.
- Keep reviewed test records in `evidence/`; never store secrets or screen content.
- Keep only the latest usable Windows and Android app deliverables in `dist/`.
- Build outputs (`bin/`, `obj/`), caches, temporary files and logs are disposable and ignored.
- Use Git history for old text/source. Use a release archive or Git LFS when old large binaries must be retained; do not keep duplicate versions in the working folder.

## Periodic check

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Project.ps1
```

Use `-RequireArtifacts` when the latest local EXE and APK must both be present and hash-correct.