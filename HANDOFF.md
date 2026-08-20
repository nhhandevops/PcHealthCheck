# HANDOFF — PcHealthCheck

Snapshot for continuing work on another machine. Last updated: **2026-08-20** (commit `32bae61`, 121 tests passing, Release builds clean with `-warnaserror`).

## What this is

An offline-by-default, read-only-by-default Windows PC health auditor (.NET 10 + Avalonia 12). One app that grades hardware + software health (Good / Normal / Bad) with fix tips, failure forecasting, per-module consent, and a post-scan receipt of everything it touched. Full background: [README.md](README.md), [PRIVACY.md](PRIVACY.md), [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Repository

`https://github.com/nhhandevops/PcHealthCheck` — **private**, default branch `main`. CI (`build`) runs on every push: build with `-warnaserror`, tests, the no-`System.Net` privacy gate, then a single-file self-contained publish with SHA-256 checksums uploaded as an artifact.

## Getting started on a fresh machine

```text
winget install Microsoft.DotNet.SDK.10        # .NET 10 SDK (LTS) — the only prerequisite
gh repo clone nhhandevops/PcHealthCheck       # private: needs `gh auth login` first
dotnet build PcHealthCheck.slnx               # note: .slnx, the new solution format
dotnet test  PcHealthCheck.slnx               # 121 tests; WorkerProtocolTests launch the real exe
dotnet run --project src/PcHealth.App         # GUI
PcHealthCheck.exe --headless-scan [--include-admin] [--no-history] --out <dir>   # automation mode
```

## Status by milestone

| Milestone | State |
|---|---|
| M1 — 12 unelevated modules, verdict engine (JSON rules), consent matrix, receipt, HTML/JSON export | ✅ done |
| M2 — split-privilege elevated worker (`--worker`, named pipe, parent-PID check, compiled-in registry) + 5 admin modules (SMART, reliability counters, backup posture, platform security, integrity) | ✅ done |
| QA hardening — locale bugs fixed, analyzers CA1305/CA1304/CA1310 = **errors**, worker session caps | ✅ done |
| M2.5 — SQLite trend history: growth on failure counters ⇒ Bad, wear/battery projections after 14+ days | ✅ done |
| M3 part 1 — sensors via LibreHardwareMonitorLib 0.9.6 (`LoadsDriver` capability, per-device airflow check) | ✅ done |
| Dependency manager — consented installs (command shown), safe exit removal, 15-day text history | ✅ done |
| M4 part 1 — malware history (`malware.defender`) + network integrity (`network.health`) | ✅ done |
| **M4 part 2 — on-demand Defender scan, ClamAV via the dependency manager, opt-in VirusTotal hash lookups** | ⬜ next |
| M3 part 2 — benchmarks (DiskSpd read-only disk profile, 7-Zip CPU bench, personal baselines) | ⬜ |
| Packaging — SignPath OSS signing, GitHub Release + checksums, winget manifest, SmartScreen submission | ⬜ |
| M5 — browser extensions, multi-user startup, localization (Vietnamese), Linux provider | ⬜ |

## Architecture in five lines

- `PcHealth.Core` — module contracts (+capability hash for re-consent), verdict engine driven by `src/PcHealth.Core/Rules/*.json`, trend history (`History/`), dependency ledger (`Dependencies/`), report exporters. **No OS or network code.**
- `PcHealth.Modules.Windows` — all Windows modules (WMI/CIM, EventLogReader, registry, P/Invoke). Locale-proof parsing only: provider+event-id, exit codes, typed objects — never localized strings.
- `PcHealth.App` — Avalonia UI (`asInvoker`, never self-elevates) + `--worker` elevated helper. Trust boundary: the worker only runs `NeedsAdmin` modules from its own compiled-in registry; write/driver actions get the worker's own confirmation dialog.
- `PcHealth.Ipc` — length-prefixed JSON pipe protocol between UI and worker.
- `PcHealth.Net.Optional` — the ONLY assembly allowed to reference `System.Net` (CI gate enforces; even comments count). Still a placeholder until the VirusTotal module lands.

## Non-obvious rules that must survive

1. **Trend analysis order**: `HistoryService.AnalyzeAndRecord` compares against history BEFORE recording the current scan — reversed, every counter looks stable forever.
2. **History pruning** keeps the oldest scan always (projections measure from it) and caps at 500 scans.
3. **Dependency ledger**: only tools THIS app installed enter `dependencies.json`; pre-existing tools are structurally unremovable. Exit cleanup refuses removal while FanControl/OpenRGB/LHM run. Removal log `dependency-history.txt` keeps 15 days.
4. **Capability hash** (admin/writes/internet/driver) invalidates stored consent when a module's powers change — never widen a module silently.
5. **LibreHardwareMonitorLib must stay ≥ 0.9.5** (0.9.4's WinRing0 driver is quarantined by Defender). It ships NO driver; CPU/motherboard sensors need user-installed PawnIO (`winget install namazso.PawnIO`) — the app offers this via the dependency manager.
6. Locale analyzers are errors on purpose; this app parses tool output and must behave identically on Vietnamese/German/English Windows.
7. **A check that could not finish must never read as a clean one.** A module whose query fails returns `PartiallyCompleted`, and both the HTML report and the UI print "Some checks could not finish… that is not the same as being healthy" *above* the results. Locked by `ReportExportTests`, in both directions — the warning must appear when something broke and must NOT appear on a clean run.
8. **Logic that only runs on an unhealthy PC needs unit tests, because a dev machine never executes it.** Keep such logic in a pure class taking plain records (`Security/ThreatAssessment.cs` is the pattern) so the WMI layer stays a thin adapter over tested code.

## Bug log — two failures found only by running on real hardware

Both were found on 2026-08-20 by running the app on the dev PC rather than by any test. Written down because both classes of mistake are easy to make again.

**1. `ThreatStatusID` was queried on the wrong WMI class.** It lives on `MSFT_MpThreatDetection`, not `MSFT_MpThreat`. WMI answered "Invalid query", the module swallowed it, two threat metrics silently vanished — and the report still showed Security as healthy. *Three things now stop a repeat:* every one of the 29 WMI queries was audited property-by-property against the live schemas (27 validated directly, 2 admin-only proven by an elevated scan); the join/status logic was extracted to `ThreatAssessment` with 11 tests; and a failed query now degrades the module's status so its absence is stated out loud (rules 7 and 8 above). **When adding a WMI query, verify each property against the live class first** — `Get-CimClass <Name> | % { $_.CimClassProperties.Name }` — do not trust documentation or memory.

**2. A false "poor airflow" alarm on an idle RTX 3060.** The check compared *all* fans against *any* sensor over 60 °C, so a 72 °C GPU hotspot (normal, and hot by design) made stopped fans look like a failure. Fixed by grading airflow per device with hot-by-design sensors excluded and per-class thresholds (`GpuFansExpectedAboveC = 80`, `CpuFansExpectedAboveC = 70`). **A threshold that a healthy machine trips is a bug in the threshold, not a finding** — and false alarms are more corrosive here than missed ones, because they teach the user to ignore the app.

## Known machine-specific facts (original dev PC, MSI board, i-something + RTX 3060)

- CPU temperature needs PawnIO (not installed there yet as of the snapshot date; app offers it in a banner).
- Baseline findings: component store "Repairable" (fix: `DISM /Online /Cleanup-Image /RestoreHealth` then `sfc /scannow`), 0 restore points, no BitLocker, 69 disk I/O events + 9 unexpected shutdowns per 90 days with clean SMART on all 3 drives.

## Gotchas (each cost real time)

- `dotnet test` won't rebuild `PcHealth.App` unless the test project references it — already wired with `ReferenceOutputAssembly=false`; don't remove it.
- The GUI is WinExe: to capture headless output use `Start-Process -Wait -RedirectStandardOutput`.
- `SLIsGenuineLocal` lives in **slwga.dll**, not slc.dll.
- Unelevated `MSFT_MpPreference` returns "N/A: Must be an administrator…" placeholder strings instead of failing.
- Multi-line git commit messages with quotes: write to a file, `git commit -F file` (PowerShell here-strings break).
- xUnit here is v2: no `Assert.SkipWhen`.
- CI (`.github/workflows/build.yml`) has never run yet — it activates on first push. It builds `-warnaserror`, runs tests, enforces the System.Net gate, publishes a single-file exe with SHA-256 checksums.

## Suggested next session

Start M3 part 2: bundle `diskspd.exe` (MIT) in `third_party/` with its license text, add a `disk.speed` module (read-only profile: `-w0`, 1 GiB temp file created+deleted with receipt entries, skip SMART-flagged drives, separate opt-in for write tests), store first run as a personal baseline in the history DB, and flag >30–40% sustained regression. Then the 7-Zip (`7za b`) CPU benchmark.
