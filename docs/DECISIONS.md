# Durable decisions

Record decisions that future work must respect. Current task status belongs in `../STATE.json`.

| ID | Decision | Reason | Status |
| --- | --- | --- | --- |
| D-001 | Release 1 is attended and same-LAN only | Keeps consent, networking and support boundaries understandable | Active |
| D-002 | Host approval and fingerprint comparison are mandatory | A short PIN alone is not a complete PAKE | Active |
| D-003 | View-only is the default; control needs a separate host grant | Prevents unintended OS input | Active |
| D-004 | JPEG is the supported network codec | The old H264Sharp wrapper and patched OpenH264 library have incompatible ABIs | Active |
| D-005 | Windows WPF remains the host shell; MAUI is the mobile viewer path | Matches current platform permissions and maintained code | Active |
| D-006 | Internet access is a separate product phase | Signaling, TURN cost, accounts and abuse controls require a new threat model | Active |
| D-007 | Physical evidence and trusted signing decide release readiness | Loopback checks cannot prove real LAN, input, DPI or installation behavior | Active |
| D-008 | `STATE.json` is the single current-fact record | Prevents conflicting versions and blockers after AI context compaction | Active |

## How to change a decision

Do not rewrite history. Add a new row that supersedes the old ID, explain why, update `ARCHITECTURE.md` if the system shape changes, and add one `WORKLOG.md` entry.