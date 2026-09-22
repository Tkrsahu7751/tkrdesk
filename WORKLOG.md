# Apna Remote — worklog

Keep newest entries first. Record outcomes, evidence and remaining risk; do not paste chat transcripts or full command output. Move old entries to `docs/history/YYYY-WORKLOG.md` only when this file becomes hard to scan.

## 2026-09-21 — Real Windows Printer Auto-Detection & Smart Auto-Select

- **Real Windows Printer Discovery:** Created `LocalPrinterService.cs` using native `System.Printing.LocalPrintServer` to scan all physical and virtual Windows printers without third-party dependencies.
- **Host Auto-Select:** If only 1 printer or 1 physical printer exists on the Host PC (e.g. `Brother DCP-T220 (USB002)`), it is automatically checked/selected by default. Added `[Select All]`, `[Deselect All]`, and `[🔄 Refresh]` buttons for multi-printer office environments.
- **Spooler Protocol Enhancement:** `QueueStatusMessage` now broadcasts `SharedPrinters` list over TCP 8989, and `SUBMIT` jobs carry `TargetPrinter` in payload.
- **Client Auto-Select:** When workstations connect to the Host queue server, if only 1 printer is shared, it is automatically selected (`Target: Brother DCP-T220 (Auto-selected ✓)`). If multiple printers are shared, an interactive `Target Printer` dropdown is presented.
- **Verification:** 86/86 automated checks passed (0 warnings, 0 errors). Published new binary (`DD0CBCD9...`) and updated desktop installation.

## 2026-09-21 — Thread Deadlock & UI Freeze Fix (Smart Queue & Hardware Hub)

- **Root Cause Identified:** When a job completed processing, `SmartQueueManager.WorkerLoopAsync` invoked `QueueChanged` while holding `lock (_gate)`. In `MainViewModel`, `QueueChanged` called synchronous `Dispatcher.Invoke`, which caused the UI thread to wait for `_gate` in `GetAllJobs()` while the background worker held `_gate` and waited for the UI thread, causing a complete application freeze/hang.
- **Deadlock Eliminated:** 
  - Refactored `SmartQueueManager.cs` (`WorkerLoopAsync`, `CancelJob`, `ClearFinishedJobs`) to guarantee that all events (`QueueChanged`, `JobCompleted`, `JobCancelled`, etc.) are ALWAYS fired outside `lock (_gate)`.
  - Switched all background-to-UI dispatching in `MainViewModel.cs` from synchronous `Dispatcher.Invoke` to asynchronous fire-and-forget `Dispatcher.BeginInvoke`.
- **Smooth Client Status Polling:** Added background polling in `SubmitQueueJobCommand` so the client card smoothly transitions from "Submitting" ➔ "Processing" ➔ "✅ Job completed successfully!" without requiring manual refresh.
- **Verification:** 86/86 automated checks passed, published new binary (`E8F5F6F2...`), and updated local installation.

## 2026-09-21 — LAN Auto-Discovery & Pure Generic USB Hub

- **Automatic LAN Office PC Discovery:** Implemented `HubDiscoveryHost` (UDP 8991 listener) and `HubDiscoveryClient` (broadcast scanner) enabling zero-config automatic detection of office PCs on local subnet.
- **Smart Auto-Fill for Client Cards:** Added "🔍 Scan LAN" banner, dynamic status reporting, and one-click auto-fill chips (`DiscoveredHubs`) across Smart Office Queue, Pen Drive Broadcaster, and USB Peripherals Tunnel cards. Includes quick "This PC (127.0.0.1)" button for instant local loopback testing.
- **Pure Generic USB Hub Refactor:** Completely removed all references to "Biometric" and "Fingerprint" from enums, UI ComboBoxes, and logs in full compliance with user instructions and UIDAI/banking guidelines. Generic USB architecture handles any standard USB peripherals without specialized sensitive classifications.
- **Firewall & Build Verification:** Updated `Allow-Firewall.bat` with UDP 8991. Passed 86/86 automated checks (30 core, 17 discovery, 17 host, 22 viewer) with 0 warnings and 0 errors. Re-published and updated desktop install.

## 2026-09-21 — Comprehensive Code & UI Audit (Hardware Hub, Buttons, & Error Recovery)

- Conducted comprehensive code and UI audit across all features, commands, bindings, and error handlers.
- Fixed Sub-tab navigation UX: Active hardware mode is now clearly highlighted with dynamic status badges (`QueueTabButtonText`, `PenDriveTabButtonText`, `UsbTabButtonText`).
- Fixed Client Submission Form: Added missing `QueueClientCategory` dropdown (Printer, Scanner, Plotter/Cutter, Other) to office workstation submission grid.
- Enhanced Pen Drive Explorer: Added dynamic `ActionLabel` ("Open 📂" for directories vs "Download ⬇️" for files) and added one-click "📥 Downloads Folder" opener.
- Hardened Network Error Handling: Fixed exception recovery in `NetworkSpoolServer.Start()` and `PenDriveServerService.Start()` to ensure complete teardown on socket collision.
- Expanded Firewall Configuration: Updated `Allow-Firewall.bat` to permit incoming traffic for Smart Office Queue (8989), Pen Drive Broadcaster (8990), and USB tunnel (3240).
- Renamed navigation menu for maximum clarity: "Overview" is now **"Remote Desktop"** (AnyDesk style screen sharing & remote control) and "Devices" is now **"USB Hub"** (Hardware & office sharing hub).
- Passed full automated suite (86/86 tests passing, 0 warnings, 0 errors). Re-published `dist\ApnaRemote-TwoPcLab` and refreshed desktop installation.

## 2026-09-13 — durable AI project memory

- Added root AI instructions, machine-readable state, scoped native rules and a repository checker.
- Separated architecture, decisions, roadmap, testing, user, release, privacy and maintenance documents under `docs/`.
- Kept current truth in `STATE.json` and removed repeated status text from stable documents.
- Updated packaging and UI document references; no source behavior, build, artifact or Git history was changed.

## 2026-09-13 — exact cleanup

- Removed obsolete duplicate plans, old generated outputs and abandoned codec material.
- Kept one current Windows EXE and one current Android APK; retained only their required package companions.
- Preserved three reviewed session metric files under `evidence/metrics`.
- Freed about 1.6 GB while retaining local SDK and NuGet dependencies needed for offline builds.

## 2026-09-13 — 0.20.6 automated checkpoint

- Release build reported 0 warnings and 0 errors.
- Core 30/30, discovery 17/17, host lifecycle 17/17 and viewer lifecycle 22/22 passed.
- Current-version two-PC testing, 30-minute soak and signed clean-machine install remain pending.