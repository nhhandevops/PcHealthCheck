# HANDOFF — PcHealthCheck

Snapshot for continuing work on another machine. Last updated: **2026-08-22** (269 tests passing, Release builds clean with `-warnaserror`).

## What this is

An offline-by-default, read-only-by-default Windows PC health auditor (.NET 10 + Avalonia 12). One app that grades hardware + software health (Good / Normal / Bad) with fix tips, failure forecasting, per-module consent, and a post-scan receipt of everything it touched. Full background: [README.md](README.md), [PRIVACY.md](PRIVACY.md), [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Repository

`https://github.com/nhhandevops/PcHealthCheck` — **private**, default branch `main`. CI (`build`) runs on every push: build with `-warnaserror`, tests, the no-`System.Net` privacy gate, then a single-file self-contained publish with SHA-256 checksums uploaded as an artifact.

## Getting started on a fresh machine

```text
winget install Microsoft.DotNet.SDK.10        # .NET 10 SDK (LTS) — the only prerequisite
gh repo clone nhhandevops/PcHealthCheck       # private: needs `gh auth login` first
dotnet build PcHealthCheck.slnx               # note: .slnx, the new solution format
dotnet test  PcHealthCheck.slnx               # 269 tests; WorkerProtocolTests launch the real exe
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
| M4 part 2 — on-demand Defender quick/folder scans + opt-in VirusTotal reputation lookups | ✅ done |
| M3 part 2 — benchmarks (DiskSpd disk profile, 7-Zip CPU bench, personal baselines) | ✅ both verified on real hardware |
| M5 — browser extension audit | ✅ done |
| M5 — multi-user startup coverage | ✅ shipped and verified elevated |
| **M5 remainder — Vietnamese localization, Linux provider** | ⬜ next |
| ClamAV second opinion | ❌ dropped by decision (2026-08-20) — see below |
| Packaging — `build-release.ps1`, icon, version 1.0.0-rc.1, docs | ✅ done |
| **Acceptance testing — `ACCEPTANCE.md`, then signing and publishing per `RELEASING.md`** | ⬜ next |

## Architecture in five lines

- `PcHealth.Core` — module contracts (+capability hash for re-consent), verdict engine driven by `src/PcHealth.Core/Rules/*.json`, trend history (`History/`), dependency ledger (`Dependencies/`), report exporters. **No OS or network code.**
- `PcHealth.Modules.Windows` — all Windows modules (WMI/CIM, EventLogReader, registry, P/Invoke). Locale-proof parsing only: provider+event-id, exit codes, typed objects — never localized strings.
- `PcHealth.App` — Avalonia UI (`asInvoker`, never self-elevates) + `--worker` elevated helper. Trust boundary: the worker only runs `NeedsAdmin` modules from its own compiled-in registry; write/driver actions get the worker's own confirmation dialog.
- `PcHealth.Ipc` — length-prefixed JSON pipe protocol between UI and worker.
- `PcHealth.Net.Optional` — the ONLY assembly allowed to reference `System.Net` (CI gate enforces; even comments count). Holds the VirusTotal client and `NetworkActivityLog`, which counts every request the app makes so the receipt can report a checked number instead of a promise.

## Non-obvious rules that must survive

1. **Trend analysis order**: `HistoryService.AnalyzeAndRecord` compares against history BEFORE recording the current scan — reversed, every counter looks stable forever.
2. **History pruning** keeps the oldest scan always (projections measure from it) and caps at 500 scans.
3. **Dependency ledger**: only tools THIS app installed enter `dependencies.json`; pre-existing tools are structurally unremovable. Exit cleanup refuses removal while FanControl/OpenRGB/LHM run. Removal log `dependency-history.txt` keeps 15 days.
4. **Capability hash** (admin/writes/internet/driver) invalidates stored consent when a module's powers change — never widen a module silently.
5. **LibreHardwareMonitorLib must stay ≥ 0.9.5** (0.9.4's WinRing0 driver is quarantined by Defender). It ships NO driver; CPU/motherboard sensors need user-installed PawnIO (`winget install namazso.PawnIO`) — the app offers this via the dependency manager.
6. Locale analyzers are errors on purpose; this app parses tool output and must behave identically on Vietnamese/German/English Windows.
7. **A check that could not finish must never read as a clean one.** A module whose query fails returns `PartiallyCompleted`, and both the HTML report and the UI print "Some checks could not finish… that is not the same as being healthy" *above* the results. Locked by `ReportExportTests`, in both directions — the warning must appear when something broke and must NOT appear on a clean run.
8. **Logic that only runs on an unhealthy PC needs unit tests, because a dev machine never executes it.** Keep such logic in a pure class taking plain records (`Security/ThreatAssessment.cs` is the pattern) so the WMI layer stays a thin adapter over tested code.
9. **Privacy claims are code, not copy.** When a feature changes what the app can do, the claims move with it in the same commit: PRIVACY.md, the HTML report line, and the receipt. Prefer a counted number (`NetworkActivityLog.Count`) over an absolute a later feature could quietly falsify. "Network connections during this scan: 0" stays true by construction — no check contains network code — and the CI gate is what keeps it that way.
10. **A cap must say what it dropped.** The VirusTotal lookup is capped at 20 files for the free tier, and the consent dialog states how many were left unchecked. Silent truncation reads as "everything was checked".

## Benchmarks (M3 part 2)

- **Tools are offered, not bundled.** `Microsoft.DiskSpd` (MIT) and `7zip.7zip` (LGPL) go through the dependency manager, so the user sees the install command, removal is offered at exit, and no third-party binaries live in this repo. `ToolLocator` finds them via winget's `Links` shim, the versioned `Packages` folder, Program Files, or PATH.
- **`bench.disk` is the only check that writes to the user's drives.** One test file per *physical* drive (not per volume — C: and E: on one SSD would write twice for the same answer), created by DiskSpd with `-c`, deleted in a `finally`, and skipped entirely unless the drive has 15% headroom beyond it. Every profile passes `-w0`, so the measured phase only reads.
- **Bus type is checked before media type**, and that order matters: on the dev PC a 2.5" spinning drive in a USB caddy reports `MediaType 4` (SSD). Trusting it would grade a healthy 100 MB/s hard disk against a 400 MB/s floor. Anything on USB/SD/MMC is classed `External` and gets no floor at all.
- **What is graded and what is not.** Only sequential read against the bottom of the drive's class. Random 4K, external drives, and the CPU score are reported `InfoOnly`, because a fair absolute threshold for them depends on the exact model. The real signal is `TrendKind.PerformanceDrop`: each benchmark is compared against the **median** of that machine's own history (median, not first or best — one busy afternoon must not become the baseline), needing 2+ prior samples, flagging below 70% and calling it Bad below 50%.
- **Each watched metric carries its own advice.** A CPU regression points at temperatures and power mode; a disk regression points at free space and cabling. A test asserts the two never cross.
- **`ToolLocator` picks the build matching this processor.** DiskSpd's winget package ships `amd64/`, `arm64/` and `x86/` side by side; a plain recursive search returns whichever the file system hands back first. If only other architectures are present it returns null rather than running something that cannot start. Also note winget reports `Command line alias added: diskspd` while leaving `Links` **empty** — the `Packages` folder is the path that actually works.

### Verified on the dev PC, 2026-08-21

Full run: 151 s for four physical drives, all four test files deleted, receipt truthful, `-w0` on every measured pass.

| Drive | Sequential | Random 4K | Verdict |
|---|---|---|---|
| D: ST2000DM008 (SATA HDD) | 209.9 MB/s | 1.92 MB/s | Good (262% of class floor) |
| E: CT1000P3PSSD8 (Gen4 NVMe) | 4,329.5 MB/s | 646 MB/s | Good (241%) |
| G: ST1000LM035 (HDD in USB caddy) | 114.1 MB/s | 1.14 MB/s | not graded — external |
| F: Realtek RTL9210B-CG (NVMe in USB enclosure) | **38.3 MB/s** | 15.3 MB/s | not graded — external |

Two things this run proved. **G: is the classification fix earning its keep** — Windows reports that spinning drive as `MediaType 4` (SSD), and grading it against the 400 MB/s SATA-SSD floor would have called a healthy drive 28% and Bad. And **F: is a real finding on the dev PC**: 38 MB/s with 15 MB/s random is the signature of a USB 2.0 link, not of an NVMe drive. The report said "38.3 MB/s" and left the reader to notice, so `DriveClassifier.DescribeExternalLink` now names it — worded as an observation, because a 30 MB/s memory stick reading 30 MB/s is working perfectly.

## Startup for other accounts, and the access trap underneath it

`startup.allusers` (admin) closes a real hole: the ordinary startup check only sees the account it runs as, so on a shared PC anything set to run for someone else's account is invisible. It reads each profile's Run/RunOnce keys — reachable only while Windows has that account's hive loaded, i.e. while they are signed in — plus every account's Startup folder, which works either way.

**The trap, found on the dev PC's second account:** when a profile folder is denied, .NET reports paths *beneath* it as **missing**, not forbidden. `Directory.Exists(<their>\…\Startup)` returns `false` for an account whose contents were never visible — so the obvious implementation reports "nothing starts up for this account" about an account it never actually looked at. `UserProfiles.CanRead` therefore probes the profile root by opening it, and the module reports the account as unchecked rather than clean. Locked by a test that runs against whatever real profiles the machine has.

Service identities are filtered out by SID: only `S-1-5-21-…` with RID ≥ 500 is a person. SYSTEM, LOCAL SERVICE, NETWORK SERVICE and the `S-1-5-80-…` per-service SIDs all have profile folders and would otherwise appear as accounts nobody recognises — which is how a real finding gets lost.

**Verified elevated on the dev PC, 2026-08-21** — and the first real run caught a third instance of this codebase's recurring bug. The report said *"Programs starting for other accounts: 0 — Nothing is set to start for any other account on this PC"* while, three lines above, admitting it could not read that account's registry at all. The count and the sentence beside it now come from `Startup/OtherAccountReport.cs`, a pure class with tests in both directions: a zero from a partial search says "nothing was found in the parts that could be read" plus how many accounts were incomplete, and a zero from a complete search is still allowed to say plainly that nothing is there. An app that hedges even when it did look properly is an app whose warnings stop meaning anything.

**Older note — how to re-run it:** an elevated end-to-end run. `PcHealthCheck.exe --headless-scan --include-admin --no-history --out <dir>` (one UAC prompt), then check `startup.other_accounts` lists the second account, and that an account whose hive is not loaded produces a note saying so rather than a clean bill. **Known gap:** an account that is not signed in has no loaded hive, so only its Startup folder is covered. Closing that needs `RegLoadAppKey` to open `NTUSER.DAT` read-only and privately — deliberately not done yet, since it is the first time the app would touch another user's registry file at all.

## The CPU benchmark measures one core, and that was a measurement not a preference

Verified against real 7-Zip on 2026-08-21, and the verification changed the design.

7-Zip's default all-core run looked like the obvious choice. Five back-to-back runs on the same idle PC said otherwise:

| all-core (`b`) | single-core (`b 1 -mmt1`) |
|---|---|
| 52,267 / 43,768 / 41,573 / 36,715 / 28,745 MIPS | 6,026 / 5,853 / 5,914 / 6,074 MIPS |
| **45% spread**, ~32–60 s, fans audible | **3.6% spread**, ~26 s, quiet |

A minute of all-core work heat-soaks the chip, so each run measures the previous run's heat. The `PerformanceDrop` rule sits at 30%, which that spread would have tripped constantly — a benchmark whose own effect dominates its result detects nothing. Through the whole app, twice: 6,084 and 6,232 MIPS.

Single-core is also what most software actually feels, and a processor that is throttling or stuck on a power-saving plan still shows it. Cooling under sustained load is the sensors module's job, not this one's.

The metric id changed with it — `bench.cpu.rating_mips` → `bench.cpu.single_thread_mips` — because 52,000 and 6,000 are not the same measurement, and per the rule below a changed method needs a new id or the trend analyzer reports it as a failing processor.

## Runbook — verifying the CPU benchmark against real 7-Zip

Kept for re-verifying after a 7-Zip update, or on a machine where the check misbehaves. Everything below takes about five minutes. (The original verification was done without installing anything: 7-Zip's standalone `7za.exe`, unpacked from the `7z2602-extra.7z` release asset using the `7zr.exe` bootstrapper, both public downloads from the ip7z/7zip repo.)

**Why it was left:** `7zip.7zip` is a WiX MSI that installs into Program Files, so it needs one UAC prompt. DiskSpd needed none (portable package), which is why the disk half could be verified unattended and this half could not.

**Step 1 — install 7-Zip.** Preferred route is the app's own purple banner → **Install…**, because that exercises the dependency-manager flow at the same time and records the install in the ledger, so the app offers to remove it again when you close it. The equivalent by hand, which the app shows you before running it:

*Or install nothing at all:* the locator also accepts 7-Zip's standalone `7za.exe`, so unpacking the "7-Zip Extra" archive anywhere on PATH is enough and needs no UAC prompt. Useful for verifying the parser without touching the machine.

```text
winget install 7zip.7zip --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
```

**Step 2 — confirm the locator finds it.** `SevenZipBenchmark.Find()` looks in Program Files, Program Files (x86), winget's `Links` shim, the `Packages` folder, then PATH. A quick check:

```powershell
Test-Path "$env:ProgramFiles\7-Zip\7z.exe"
```

**Step 3 — capture what the parser will actually be given.** This is the step that caught the DiskSpd bugs; do not skip it in favour of trusting the fixture:

```powershell
& "$env:ProgramFiles\7-Zip\7z.exe" b | Select-Object -Last 8
```

Expect a table ending in two summary rows. `SevenZipOutputReader` keys on exactly these:

```text
Avr:      36004   621   5399  33548  |  390298   791   4569  36171
Tot:              706   4984  34859
```

- `Tot:` — **last** number is the overall rating in MIPS.
- `Avr:` — must hold **exactly 8** numbers; compression rating is the 4th, decompression the 8th. Any other column count and the parser deliberately reports 0 for both rather than reading the wrong column.

**Step 4 — run the check and compare.**

```powershell
PcHealthCheck.exe --headless-scan --no-history --out <dir>
```

Then in the JSON report, `bench.cpu.rating_mips` must equal the `Tot:` figure from step 3, and `bench.cpu.compress_mips` the `Avr:` 4th column. On the dev PC (i5-12400F, 6c/12t) expect roughly 30,000–45,000 MIPS; a laptop will be far lower and that is not a fault — the metric is `InfoOnly` precisely because MIPS only means something against the same processor.

**If it fails**, the likely cause is the `Avr:` column count differing in a newer 7-Zip. That is the designed-for case: `bench.cpu.rating_mips` still appears (it only needs `Tot:`) and the two compression figures are simply absent. Fix `SevenZipOutputReader.Parse` and add the real row shape to `BenchmarkTests.SevenZipOutput`.

**Not yet covered:** the run loads every core for about a minute, so the fans will spin up — that is the test working, and it is also why a CPU regression later points at temperatures first.

## Follow-up audit of the three bugs (2026-08-22)

Re-checked the previous day's three findings against the standing bar — *is it tested, did I look for siblings, and does the app say so if it recurs?* Two came back clean; all three turned up something.

**1. The benchmark creating the faults it reports.** Tested (three tests lock the queue depth and file size) and the module warns that benchmarking can inflate the disk-error count. **Sibling found:** the plan always said "skip SMART-flagged drives" and it was never implemented — the benchmark would happily write a gigabyte to a drive Windows already reports as failing, while every other part of this app tells the user to back such a drive up and stop using it. `MSFT_PhysicalDisk.HealthStatus` turns out to be readable unelevated, so the check now refuses those drives and says why.

**2. The CPU benchmark measuring its own heat.** Tested. **Sibling measured rather than assumed:** the disk metrics sit on the same watch list at the same 30% rule. Back-to-back on an idle PC, sequential repeats to within **1.1%** and scattered reads to within **11.1%** — but scattered reads swung **26%** between two healthy full scans, which is almost no margin against 30%. The watch list now carries a per-metric noise floor and scattered reads get 45%. Sequential keeps 30% because it earns it.

**3. A zero meaning "clean" when it meant "not checked".** Tested via `OtherAccountReport`. **Siblings found by auditing every module's failure paths:** `VolumesModule` and `LicenseModule` both added a note when a query failed and still returned `Completed`, so the "Some checks could not finish" banner never appeared. Losing `MSFT_Volume` silently drops file-system *health* (the fallback only reports free space); an unparseable `fsutil` TRIM line on a non-English Windows silently drops TRIM; a failed licensing query silently drops both activation status and the fake-KMS-server check. All now degrade the module. `DriversModule` was already correct (returns `Failed`), and `SecurityPostureModule` and `NetworkHealthModule` correctly suppress the metric instead of emitting a misleading zero.

**The distinction that matters when adding these:** *"I deliberately did not check this here"* (TRIM needing admin, deferred to the elevated scan) must NOT degrade status, or the banner fires on every normal scan and stops meaning anything. Only *"I tried and it broke"* counts. Verified on the dev PC: `volumes` and `license` still report `Completed`, so the stricter reporting does not cry wolf.

## The benchmark must not create the fault it reports

The single most important thing learned building M3 part 2, found only by running it four times on a real PC.

The disk benchmark was generating **~22 Windows I/O retry events per run** on a 2.5" hard disk in a USB caddy — and the app's own event-log check then counted them and graded the machine **Bad**. The 90-day baseline was 69 such events; one evening of testing added over 100.

Two wrong guesses preceded the fix, both worth recording because the reasoning looked sound:

1. *"The deep queue is too aggressive"* — `-b4K -o32 -t4` on a platter. Softening it to `-o4 -t1` cut retries from 22 to 14, so it looked right. It was a real improvement (deep queues are meaningless on a single head) but not the cause.
2. *"Then it must be the random pass"* — skipping it entirely made things **worse** (23), which finally forced a measurement instead of another guess.

A three-command probe settled it: creating the 1 GiB test file produced **32 retries**, the sequential read pass **0**, the random read pass **0**. The measured phase was never the problem — the *write* was. Without admin rights DiskSpd cannot set a file's valid length and must physically write every byte, so the only lever is fewer bytes. External drives now get a 256 MiB file (25 retries → 4 in isolation, ~22 → 2 across a whole scan) and the module says out loud that a benchmark can inflate the disk-error count, so the reader is not left to blame their drive for something this app did.

**Rule that follows:** any check that exercises hardware must be measured against the app's own detectors before it ships. And when a fix "improves" a number, that is not evidence the cause was found — isolate it.

**Benchmark settings are part of the measurement.** Changing queue depth or file size makes new readings incomparable with recorded history (`D:` moved 210 → 168 MB/s purely from `-o8` → `-o2`). Any future change to these parameters needs a new metric id, or that metric's history cleared, or the trend analyzer will report a methodology change as a failing drive.

## Why ClamAV was dropped

The plan called for a bundled ClamAV as a third malware layer. The project owner decided on 2026-08-20 not to ship it. Two things argue against it, and it should not be added back without answering them:

1. **It adds attack surface to an app whose promise is that running it cannot harm your data.** ClamAV 1.5.4 (the current winget build, `Cisco.ClamAV`) is itself a patch for five CVEs — heap and stack overflows in the ZIP, GPT and PESpin parsers, reachable *by scanning a malformed file*. Pointing a second parser at untrusted files is precisely the situation those bugs fire in.
2. **The gap it filled is already covered.** The Defender folder scan runs with `-DisableRemediation`, which also makes it scan inside archives; the VirusTotal lookup covers ~70 engines, ClamAV among them. What is left is a weaker engine needing a ~150 MB signature download and a hand-written `freshclam.conf` in Program Files — a real system modification, beyond what the dependency manager was built to do.

If it is ever reconsidered, the honest shape is: catalog entry (`winget install Cisco.ClamAV`) + consented `freshclam.conf` creation shown verbatim + `clamscan --recursive --infected` graded on exit code (0 clean / 1 found / 2 error), with the config file recorded in the ledger so removal cleans it up too.

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

## What M4 part 2 shipped

- **On-demand Defender scans** via `MpCmdRun.exe` (found under the newest versioned platform folder — version-compare, never string sort). Quick scan `-Scan -ScanType 1`; folder scan `-Scan -ScanType 3 -File <path> -DisableRemediation`, which changes nothing *and* scans inside archives. Neither needs admin — measured on the dev PC: an unelevated quick scan took 130 s. Verdicts come from exit codes (0 clean, 2 found-or-incomplete, anything else = unknown and never clean) plus structured WMI records — never MpCmdRun's localized console text.
- **VirusTotal reputation lookups** on startup items: least-trusted first, deduplicated by hash, capped at 20 with the skipped count stated in the consent dialog. The user's own API key, memory-only. `NetworkActivityLog` counts every request so the receipt reports a checked number rather than a promise.
- `StartupInventory` extracted from `StartupModule` so the startup check and the reputation lookup grade the same list.

## Shipping

- `build-release.ps1` — tests, privacy gate, self-contained publish, zip, SHA-256. One command.
- `USER-GUIDE.md` — for the person running it, not for whoever builds it. Ships inside the zip.
- `ACCEPTANCE.md` — 19 blocking rows out of ~60. Run before giving the app to anyone.
- `RELEASING.md` — the steps a script cannot do: SignPath application (needs a **public** repo, takes weeks), GitHub release, SmartScreen submission, winget manifest.

Deliberately not done: no installer (nothing in Program Files or the registry — the packaging keeps the same promise the app makes about the machine), no auto-update (it would mean a connection nobody asked for), no MSIX until signing exists (an unsigned MSIX cannot be installed at all).

Deliberately **not** a single file either. It builds and runs, but the .NET host then unpacks the native libraries into a temp folder on first run — an undisclosed write, from an app whose whole argument is that it discloses every write. Seven visible files next to the exe are the more honest trade.

## Suggested next session

M5 remainder: browser-extension audit, multi-user startup coverage, Vietnamese localization, Linux provider. Packaging (SignPath OSS signing, GitHub Release with checksums, winget manifest, SmartScreen submission) can happen in parallel.
