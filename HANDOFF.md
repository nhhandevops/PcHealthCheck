# HANDOFF — PcHealthCheck

Snapshot for continuing work on another machine. Last updated: **2026-10-28, evening**.
- **Tests:** 1205 passing: 1139 Windows and 66 Linux fixture tests. Two live Linux tests run only in
  CI's `linux` job. Six of the Windows tests replay the pilot zips, so on a machine without
  `audit-results/` zips they are skipped (PilotReplayTests).
- **Plus 286 audit-kit checks in PowerShell that `dotnet test` cannot see** (see Shipping).
- Release builds clean with `-warnaserror`.

**Current job:** the company laptop audit. Three laptops are checked and reviewed, and the owner is
checking the rest.

**Engineering:** the remedies plan, [docs/REMEDIES-PLAN.md](docs/REMEDIES-PLAN.md).
- Phase 1 Step A (rows 1-13) was built, reviewed and fixed on 2026-10-27/28. It is committed and on
  the stick.
- **Step B, the advice engine, was built on 2026-10-28.** It is not yet committed or released.
- Step D is next.

Start at "Suggested next session" at the bottom, then the top of NEXT-STEPS.

## What this is

An offline-by-default, read-only-by-default Windows PC health auditor (.NET 10 + Avalonia 12). One app that grades hardware + software health (Good / Normal / Bad) with fix tips, failure forecasting, per-module consent, and a post-scan receipt of everything it touched. Full background: [README.md](README.md), [PRIVACY.md](PRIVACY.md), [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Build and tooling faults that no unit test covers are logged in
[docs/warning_bug_and_solutions.md](docs/warning_bug_and_solutions.md).

## Repository

`https://github.com/nhhandevops/PcHealthCheck`: **public since 2026-10-23** (YOU-1; README opens with a "not fully tested" note), default branch `main`. GitHub Actions minutes are free for public repositories, so the October CI-minutes limit no longer applies. CI (`build`) runs on every push: build with `-warnaserror`, tests, the no-`System.Net` privacy gate, then a single-file self-contained publish with SHA-256 checksums uploaded as an artifact, kept 7 days. The upload step is `continue-on-error`: a full storage quota warns instead of failing a good commit (CI-001).

## Getting started on a fresh machine

```text
winget install Microsoft.DotNet.SDK.10        # .NET 10 SDK (LTS) — the only prerequisite
git clone https://github.com/nhhandevops/PcHealthCheck   # public, no login needed
dotnet build PcHealthCheck.slnx               # note: .slnx, the new solution format
dotnet test  PcHealthCheck.slnx               # 1207 tests (2 Linux-only, skipped on Windows; 6 pilot-replay ones skipped without the audit zips); WorkerProtocolTests launch the real exe
powershell -ExecutionPolicy Bypass -File ./tests/audit-kit/kit-tests.ps1   # 286 more: the audit kit is PowerShell, dotnet test cannot see it
powershell -ExecutionPolicy Bypass -File .\daily-smoke.ps1   # the daily check, ~1 min, appends to docs/daily-smoke.md
dotnet run --project src/PcHealth.App         # GUI
PcHealthCheck.exe --headless-scan [--include-admin] [--no-history] [--skip-benchmarks] [--temp-dir <dir>] --out <dir>   # automation mode
```

The release zip also carries the **laptop audit kit** (`audit-kit/` in the repo): `1 - Check this laptop.cmd` per laptop, then `2 - Summarize all laptops.cmd` for one table. See `AUDIT-GUIDE.md`.

**The zip is not in git** (`release/` is ignored), so on another machine build it:

```text
powershell -ExecutionPolicy Bypass -File .\build-release.ps1   # tests, privacy gate, publish, kit, zip, SHA-256 (~2 min)
# -> release\PcHealthCheck-1.0.0-rc.1-win-x64.zip : extract it onto the USB stick
```

The rebuilt exe is stamped with that machine's commit (`+d98a609…` or later), not `+af47a36`. The two builds behave the same on Windows, because nothing after `af47a36` touches Windows code. Each result zip's `kit.json` names the build that produced it.

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
| M5 — Linux provider phase 1: five unelevated modules, per-OS rules, fixture tests, `ubuntu-latest` CI job | ✅ 2026-10-23 · verified on a real kernel in CI (Ubuntu 24.04, 6.17); one cosmetic fix |
| Windows 11 readiness (`os.win11_ready`, from Windows' own appraiser verdict) and the laptop audit kit with `AUDIT-GUIDE.md` | ✅ 2026-10-23 · kit tested unelevated on the T470p; its admin path awaits the owner's pilot |
| Company laptop audit — pilot on 3 laptops, zips reviewed, 5 faults found and fixed, a 6th on re-review | ✅ 2026-10-25, 2026-10-26 · see "The first fleet audit" below |
| **Company laptop audit — the rest of the fleet** | ⬜ now, owner first; the stick is ready |
| **Remedies plan, `docs/REMEDIES-PLAN.md`: advice, guided actions, the handover check, Vietnamese** | ✅ Step A (rows 1-13) built 2026-10-27/28 by 6 agents. A five-lens review with skeptics confirmed 22 faults, all fixed, and a gate re-ran everything. 🟡 Step B (the "What you can do" advice engine) built 2026-10-28 from `docs/STEP-B-SPEC.md` by five work groups in turn. It is not committed, and its person-run checks are open (see "Suggested next session"). ⬜ Step C, Step D (intake + handover check), Step E (Vietnamese) |
| **OS-001: an unenrolled Windows 10 company laptop graded "supported until 2027-10-12"** | ✅ fixed 2026-10-27 (Step A rows 8-9, not committed yet): ESU licence or build decides, one dated table in `Support/WindowsSupport.cs`, update age from the release. The kit's `requireWindows11Ready` is off (decision 9) |
| **M5 remainder — Linux phase 2 (`PcHealth.Cli`), Vietnamese localization** | ⬜ next |
| ClamAV second opinion | ❌ dropped by decision (2026-08-20) — see below |
| Packaging — `build-release.ps1`, icon, version 1.0.0-rc.1, docs | ✅ done |
| Acceptance — first pass run 2026-08-22, 5 defects found and fixed | ✅ see `ACCEPTANCE-RUN-2026-08-22.md` |
| Acceptance — second pass 2026-08-26 on a Windows 10 laptop, 1 defect (checksum) found and fixed | ✅ see `ACCEPTANCE-RUN-2026-08-26.md` |
| Reinstall of the dev laptop (2026-10-17 → 19): restored from the backup, every step verified | ✅ |
| Daily smoke `daily-smoke.ps1`; privacy gate that could never fail, fixed (PRIV-001) | ✅ 2026-10-19 |
| Full hardware audit of a ThinkPad T470p against the app's own report: 6 misgrades fixed (HW-001), gaps listed in `docs/feature-gap-2026-10-21.md` | ✅ P1s 2026-10-21 · P2/P3 open |
| Parallel scan, rule 11 below: 19.8 s → ~9 s | ✅ 2026-10-21 |
| **Acceptance — J3 (Vietnamese Windows), J1 (a PC with no SDK), the D rows, and the 14 rows that need a person** | ⬜ next |
| Repository public, with a "not fully tested" disclaimer (YOU-1) | ✅ 2026-10-23 |
| **Then signing per `RELEASING.md`** | ⬜ after that; the public-repo prerequisite for SignPath is now met |

## The first fleet audit, and the five faults it found (2026-10-25)

**Read this before touching the audit kit.** Three ThinkPads were checked on 2026-10-23 and their
result zips reviewed on 2026-10-25. Every fault below came from reading real output; none was
caught by a test, and the codebase had 457 of them at the time.

The verdicts were right. The **reasons given to the manager were wrong**, which is the more
dangerous failure: a verdict gets sanity-checked by a human, a reason gets acted on.

| | What it did | Log |
|---|---|---|
| Drive health counted USB drives | Told the manager to replace a healthy NVMe SSD, naming the external drive the audit was running from | KIT-003 |
| Battery graded 109.7% as Good | Reported a pack nobody could measure as better than new | KIT-002 |
| Elapsed time from two clock readings | A three-minute scan recorded as `-316` seconds | KIT-004 |
| SMART blamed missing admin | Said it on an elevated scan of an NVMe-only laptop, whose drive health was already in the same report | SMART-001 |
| `Double.MaxValue` for "never scanned" | Printed a 309-digit number in two of the four HTML reports | REP-001 |
| Event-log disk errors counted every drive | Found on re-review, 2026-10-26: sent a T470p for a drive check over 47 errors that were all on two USB drives; its own SSD had logged none | KIT-005 |

Four things worth carrying forward, more than the fixes themselves:

1. **The audit kit had no tests at all.** It has 286 now (`tests/audit-kit/kit-tests.ps1`), run by
   `build-release.ps1` and by CI's `Audit kit tests` step. The kit decides what a manager is told
   to replace; it had less coverage than any other part of this project.
2. **A filter that hides readings is one edit away from hiding a failure.** The USB fix had to add
   "no drive inside this laptop reported its own health" at the same time, or a laptop whose only
   readings came from the audit drive would have read as Good.
3. **The direction of a capability list is a safety property.** The first SMART draft listed the
   buses that *can* report SMART, so an unknown bus number counted as "nothing to read here" and
   turned an unread SATA disk into a silent pass — this project's four-times-shipped bug, in a
   fix meant to prevent it. It lists the *incapable* buses now. `[InlineData(250)]` holds that line.
4. **A search that confirms the comfortable answer should be rerun.** REP-001 was first written up
   as invisible to humans, because the search looked for `1.79769...e+308` and the HTML renders the
   same value as a plain 309-digit integer.

**Deliberately not done:** softening "Did not finish: Drive SMART health" in the fleet row. Once
`disk.smart` returns `Skipped` on an NVMe-only machine the reason stops appearing by itself, and
the cases where SMART still *fails* are the ones where a drive that could have answered did not.
Reasoning in SMART-001; do not re-propose it without reading that.

**One acceptance row closed for free.** The fourth zip in that batch was the dev desktop, run
through the kit by mistake. It reported "No battery detected — desktop system, module skipped"
with the Battery category correctly absent, which is **J2's desktop half** — open since August
for want of a desktop. `ACCEPTANCE-RUN-2026-10-23.md` still shows J2 as PARTIAL; that record is
of that day's run and is left as it was.

**Do not re-run the three laptops already checked.** Their zips predate every fix, which is
expected and recorded in NEXT-STEPS; `summarize-fleet.ps1` reads them correctly now.

## Architecture in brief

- `PcHealth.Core` — module contracts (+capability hash for re-consent), verdict engine driven by `src/PcHealth.Core/Rules/*.json`, trend history (`History/`), dependency ledger (`Dependencies/`), report exporters, and since Step B the advice engine (`Advice/`: one small C# class per item, its sentences as keys in `Text/en.json`). **No OS or network code.**
- `PcHealth.Modules.Windows` — all Windows modules (WMI/CIM, EventLogReader, registry, P/Invoke). Locale-proof parsing only: provider+event-id, exit codes, typed objects — never localized strings.
- `PcHealth.App` — Avalonia UI (`asInvoker`, never self-elevates) + `--worker` elevated helper. Trust boundary: the worker only runs `NeedsAdmin` modules from its own compiled-in registry; write/driver actions get the worker's own confirmation dialog.
- `PcHealth.Modules.Linux` (net10.0, since 2026-10-23): five modules reading only `/proc`, `/sys` and `/etc/os-release`, behind `LinuxModuleProvider`. No root, no programs, no writes. Not referenced by the Windows app; phase 2 gives it a host. Its base class `HealthModuleBase` and `ModuleFailures` now live in Core, shared with the Windows modules.
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
11. **Checks run side by side; anything that measures runs alone.** Since 2026-10-21, `ScanOrchestrator.RunModulesAsync` runs up to 4 modules at once, wrapping each in `Task.Run`, because most modules do synchronous WMI work before their first await. Results come back in list order. A module that measures speed or temperature sets `RunsAlone = true` in its manifest (today: `bench.disk`, `bench.cpu`, `sensors`) and runs afterwards with nothing beside it. **A new benchmark or sensor check that forgets this flag will measure the scan instead of the PC.** On the T470p the unelevated scan went from 19.8 s to 8.4–10.4 s, with peak memory 63 → 76 MB and identical results. Locked by `ScanOrchestratorTests`, which were checked against a forced one-at-a-time cap: 4 of 7 fail. The elevated helper (`WorkerHost`) still runs its own modules one at a time, but since 2026-10-23 both scan entry points **start it first, beside the normal checks**. `RunModulesAsync(..., beforeRunsAlone:)` awaits it before the RunsAlone phase, so the benchmarks never overlap the admin checks, and sensors no longer reads a processor the CPU benchmark has just heated. The UAC prompt now appears at the start of the scan. On the T470p an elevated scan dropped from ~125 s to 103 s. Starting the helper blocks on the UAC prompt, so it is wrapped in `Task.Run`.
12. **One metric id, one rule per OS where the advice differs.** Since 2026-10-23 a rule can carry `"os": "windows"` or `"os": "linux"`, and `RulesLoader.LoadEmbedded()` keeps only the current OS's rules plus the OS-neutral ones. Free space, memory used, commit and the ACPI zone have one rule each per OS; their bands match, and only the tips differ. **A new Linux reading that reuses a Windows id inherits Windows advice unless it gets a Linux rule.** `RuleTests.Every_graded_linux_reading_has_a_rule_with_linux_advice` fails on any Windows tool name in a Linux tip; it was checked by planting "Task Manager" in one. Linux commit is graded only under `vm.overcommit_memory = 2`: under the default, going past `CommitLimit` is normal.
13. **Advice rests on grades and never changes one (Step B, 2026-10-28).** The "What you can do"
    section is worked out once, in `ScanOrchestrator.BuildReport`, after the grades. The JSON, the
    HTML, the window and the console all show that one section. A card needs at least one reading
    graded Normal or Bad; an information-only row never counts, and the engine refuses such a card
    as an item error.
    - A healthy or freshly reimaged PC gets **zero** cards (`HealthyPcAdviceTests`).
    - A drive check that broke is an amber "not measured" line; one deliberately left out is a grey
      note.
    - The sentences are keys in `Text/en.json`, and their numbers are formatted in code with the
      invariant culture. A card never quotes a reading's Detail or a trend's Message.
    - After any rule or advice change, run the pilot replay (`PilotReplayTests`, on the machine
      with the pilot zips) and check it still gives exactly one card, H1 "Back up now" on the T470p.
      It re-grades old readings with today's rules only (TEST-002).

## The sixth time, and the structural fix (2026-08-26)

Rules 7 and 8 above were audited against every module rather than against the three bugs that
prompted them. **Sixteen more instances** turned up — the same defect the project had already
shipped five times, in modules nobody had re-read since writing them.

The two worst, both verified by hand before anything was changed:

- **`BatteryModule`** — a *failed* `Win32_Battery` query returns the same empty list a desktop
  does, and the module read that emptiness as proof: it told the user in writing **"No battery
  detected — desktop system"** and returned `Skipped`, which does not raise the banner. On a
  laptop.
- **`StartupModule`** — `OpenSubKey(...Winlogon)` was read through null-conditional access, so an
  unopenable key left both values null, `IsNullOrEmpty(null)` was true for both, and the module
  emitted `startup.winlogon_default = **true**` with "Shell and Userinit have their default
  values." The *security* check for a known malware persistence point, asserting clean from a
  read that returned nothing.

A third deserves naming because the previous audit missed the module entirely:
**`NetworkHealthModule`** emitted the graded `network.proxy_clean = true` — "web traffic goes out
normally" — from a **null registry key**, asserting an absence of proxy hijacking nobody had
looked for.

**Why it kept coming back, and what changed.** Every module hand-rolled its own bookkeeping: add
a note, remember to increment a `failures` counter, remember to return `PartiallyCompleted`. The
modules that got it right (`VolumesModule`, `LicenseModule`, `MalwareDefenderModule`) did nothing
clever — they simply remembered. Sixteen did not. Fixing them one at a time is what the
2026-08-22 audit did, and it produced sixteen more.

`Support/ModuleFailures.cs` now makes recording the note and degrading the status **the same
call**, so they cannot come apart, and `Wmi.TryQuery(scope, wql, failures, what)` records its own
failure so it reaches the status whether the caller thinks about it or not.

**The over-correction is the more dangerous bug, and the tests say so in both directions.**
Making every "I did not check this" degrade would fire the amber banner on every healthy scan of
every ordinary PC — PawnIO missing, no battery in a desktop, TRIM deferred to the elevated run —
and a warning that is always on teaches the reader to ignore it. `ModuleFailures.Note` exists for
exactly that case, `ModuleFailuresTests` locks both directions, and the fix was checked
empirically: a scan on a healthy laptop with none of the three optional tools installed returns
the identical 15 Completed / 2 Skipped it returned before the change, with no banner.

**`HeadlessScan` had defect #5 verbatim, still present.** The declined-UAC-prompt fix of
2026-08-22 was applied to `MainWindowViewModel` only; the headless path still merged just the
results the elevated run produced, so a declined prompt made the check vanish and printed
"Overall: GOOD". It now synthesises a `Failed` result per absent admin module through a shared
helper, and returns documented exit codes instead of `0` unconditionally — a scan whose checks
could not finish was previously indistinguishable, to any script, from a clean one.

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

## The two machines, and what each one can and cannot do

There are two. Work moves between them by `git pull`, and the differences below decide which
tasks are possible where. Both facts lists are dated, because both have already gone stale once.

### The dev desktop (MS-7E01, checked 2026-10-26)

i5-12400F, RTX 3060, 32 GB, **Windows 11 Pro build 26300**. This is where the audit results are
reviewed and where the USB stick lives. The repository sits on `D:`.

- **PawnIO is installed** (the August note saying otherwise is retired), so CPU temperature reads
  here. A desktop, so it closes the acceptance rows that need one: J2's desktop half went this way.
- **DiskSpd is installed**, which has one permanent consequence: `daily-smoke.ps1` refuses to run
  its scan step here, forever, and the smoke reports `Scan: SKIP`. That is the smoke's single most
  valuable step. It is a deliberate trade — DiskSpd is the precondition for acceptance rows D2,
  D3, D5-D8, which are still open — but it means **real-scan coverage has to come from the other
  machine, or from a temporary `winget uninstall Microsoft.DiskSpd`**. Do not read a green smoke
  here as "a scan ran".
- **No WSL distribution and no Docker**, so Linux phase 2 (`PcHealth.Cli`) still cannot be
  verified here. It needs the Ubuntu 26.04 VM. Hyper-V could not even be queried unelevated.
- **7-Zip is not on PATH**, so the CPU benchmark finds nothing unless `7za.exe` is fetched first.
- Five lettered volumes, two of them external: `F:` (ANTS-SSD-1) and `H:` (An Disk, which carries
  the audit kit and its `Results\` folder). Anything that walks drives will see them.
- Its own health, from the kit run on 2026-10-25: overall **Bad**, and it is worth knowing why,
  because it is this app's own verdict on the machine it is built on. Component store
  **"Repairable"** — the same finding as August, still unfixed (`DISM /Online /Cleanup-Image
  /RestoreHealth` then `sfc /scannow`). **5 unexpected shutdowns** in 90 days (Kernel-Power 41)
  and **repeated Windows Update failures**. Disk I/O errors are down to 2 in 90 days from 69 in
  August. No BitLocker on any volume. SMART clean, ST2000DM008 at 48 degrees.

### The ThinkPad T470p (checked 2026-10-26)

Windows 10 Pro 22H2 (build 19045), i7-7820HQ, 16 GB. The acceptance runs of 2026-08-26 and
2026-10-23 were done here, and the laptop audit kit was first tested here. It has a battery, so it
holds the rows a desktop cannot.

- **No DiskSpd, so the daily smoke's scan step runs here.** This is the machine for real-scan
  coverage. On 2026-10-26 at `36dfe42`, the first smoke after the pilot fixes passed every step in
  147 s: 502/504 tests, 45 kit checks, and the scan (16 done, 1 skipped: `bench.disk`). Its HTML and
  JSON reports contained no runaway number, so REP-001 held on real output.
- **7-Zip is installed** in Program Files, so the CPU benchmark runs in the smoke here.
- **PawnIO is not installed**, so the processor temperature comes from the ACPI zone fallback.
- **No WSL distribution**, so Linux phase 2 cannot be verified here either.
- Its own health on 2026-10-26: overall **Bad**. The Bad readings were 13 pending updates, 7 disk
  errors in 90 days (the USB drives have produced these before), 4 low-memory events in 30 days, and
  the battery at 50%. Windows 11 is blocked by the processor (CpuFms), so the audit kit's verdict for
  it was REPLACE. Since `requireWindows11Ready` went off on 2026-10-27 (decision 9) that is a note,
  and the verdict is FIX FIRST: the battery, at 50%, is below the rule's 60%.

## Gotchas (each cost real time)

- `dotnet test` won't rebuild `PcHealth.App` unless the test project references it — already wired with `ReferenceOutputAssembly=false`; don't remove it.
- **The app outlives the audit kit's window.** It is a WinExe, not attached to the kit's console, so closing that window kills PowerShell but not the scan. The app finishes and writes its report anyway. That is why the kit passes `--temp-dir` on the stick (KIT-007).
- **A closed window and Ctrl+C stop the kit differently.** Closing the window runs no `finally`; Ctrl+C (then Enter, at a `Read-Host`) runs the `finally` but not the `catch`. Test both: the `finally` used to record every Ctrl+C as a finished run (KIT-007).
- **`@($list)` on a `List[object]` made with `New-Object` throws "Argument types do not match" in Windows PowerShell 5.1**, as a local, an argument or a property. Loop over it with `foreach ($x in $list)`, or pipe it inside the `@()` (TOOL-004).
- **Feed scripted answers to the kit through a `cmd` pipe, never a .NET `StreamWriter`.** The writer puts a byte-order mark into the pipe before the first line, and PowerShell reads it as `∩╗┐` (TOOL-003).
- The GUI is WinExe: to capture headless output use `Start-Process -Wait -RedirectStandardOutput`. **From the KIT-006 build on, that output is UTF-8, so read it as UTF-8.** Zips made by the `af47a36` and `462f176` builds hold it in ANSI (code page 1252): decode those as 1252, or every em dash reads as an invalid character. A WinExe has no console of its own, so before the fix .NET wrote the ANSI code page, and a reader using the console's code page showed every em dash as `ù`.
- `SLIsGenuineLocal` lives in **slwga.dll**, not slc.dll.
- Unelevated `MSFT_MpPreference` returns "N/A: Must be an administrator…" placeholder strings instead of failing.
- Multi-line git commit messages with quotes: write to a file, `git commit -F file` (PowerShell here-strings break).
- xUnit here is v2: no `Assert.SkipWhen`.
- **Build the release AFTER committing, never before.** The exe is stamped `1.0.0-rc.1+<HEAD>`, so a zip built on a dirty tree names a commit that does not contain the code inside it. Caught on 2026-10-25 only because the stick was checked after copying.
- **`build-release.ps1` can fail to package right after running the kit tests** — `Compress-Archive` hits a sharing violation on a `.ps1` it just copied, because running a PowerShell suite leaves Windows' script scanning warm. It retries five times and says so out loud (PKG-004). It is not a flake and not an input problem; `-SkipTests` packaging cleanly is what proves which it is.
- **The audit kit must stay ASCII and Windows PowerShell 5.1.** `audit-common.ps1` says so in its header: 5.1 reads a BOM-less script in the ANSI code page, so a literal em dash is garbled on some laptops. Build one by code point (`[char]0x2014`) when matching the app's drive subjects.
- **The pilot replay is not a replay of today's build (TEST-002).** It grades the 2026-10-23 readings
  with today's rules. Whether a reading is information only, and its id, are still the old
  module's: a USB volume at 18% is still a graded Normal there. Use it to prove advice and rule
  changes, never a module fix, and never quote its "other findings" counts as fleet facts. It reads
  `audit-results/*.zip` in place, prints no names, and is skipped where the zips are absent.
- **Scripted edits from bash mangle backslashes.** Writing `tests\audit-kit\kit-tests.ps1` through a heredoc produced a real TAB and a real BEL byte, because `\t` and `\a` were interpreted. It happened twice on 2026-10-25, the second time in the very line documenting the first. Use forward slashes in PowerShell paths - they work - or read the file back after writing it (TOOL-001).
- CI (`.github/workflows/build.yml`) runs on every push: `-warnaserror` build, tests, the System.Net gate, a single-file publish with SHA-256 checksums. When it goes red, check **which step** first (`gh run view <id> --json jobs`): on 2026-10-21 a red run was the artifact quota, not the code (CI-001). On a reinstalled machine `gh` needs `gh auth login` even when `git push` already works.

## What M4 part 2 shipped

- **On-demand Defender scans** via `MpCmdRun.exe` (found under the newest versioned platform folder — version-compare, never string sort). Quick scan `-Scan -ScanType 1`; folder scan `-Scan -ScanType 3 -File <path> -DisableRemediation`, which changes nothing *and* scans inside archives. Neither needs admin — measured on the dev PC: an unelevated quick scan took 130 s. Verdicts come from exit codes (0 clean, 2 found-or-incomplete, anything else = unknown and never clean) plus structured WMI records — never MpCmdRun's localized console text.
- **VirusTotal reputation lookups** on startup items: least-trusted first, deduplicated by hash, capped at 20 with the skipped count stated in the consent dialog. The user's own API key, memory-only. `NetworkActivityLog` counts every request so the receipt reports a checked number rather than a promise.
- `StartupInventory` extracted from `StartupModule` so the startup check and the reputation lookup grade the same list.

## Shipping

- `build-release.ps1` — tests, privacy gate, self-contained publish, zip, SHA-256. One command.
- `daily-smoke.ps1` — the daily check (added 2026-10-19), about a minute and unattended: repo vs GitHub, both privacy gates (source and compiled, see PRIV-001), Release build with `-warnaserror`, the full suite with the known flaky test re-run alone, **the audit kit's PowerShell checks, 286 since Step B (2026-10-28)** (added 2026-10-26 — `dotnet test` cannot see them, and a smoke reporting PASS without them was claiming coverage it did not have), and a real unelevated headless scan of the Release exe. Appends one row to `docs/daily-smoke.md`. It refuses to scan while DiskSpd is installed, because the disk benchmark would then write to every physical drive, USB backup drives included.
- `USER-GUIDE.md` — for the person running it, not for whoever builds it. Ships inside the zip.
- `ACCEPTANCE.md` — 19 blocking rows out of ~70. Run before giving the app to anyone. Until Step B
  added section K (two of them blocking) the sign-off said "/ 19" over 17 blocking rows (ACC-001).
- `RELEASING.md` — the steps a script cannot do: SignPath application (needs a **public** repo, takes weeks), GitHub release, SmartScreen submission, winget manifest.

Deliberately not done: no installer (nothing in Program Files or the registry — the packaging keeps the same promise the app makes about the machine), no auto-update (it would mean a connection nobody asked for), no MSIX until signing exists (an unsigned MSIX cannot be installed at all).

Deliberately **not** a single file either. It builds and runs, but the .NET host then unpacks the native libraries into a temp folder on first run — an undisclosed write, from an app whose whole argument is that it discloses every write. Seven visible files next to the exe are the more honest trade.

## What changed 2026-10-19 → 10-21, in brief

- **Memory is its own category now** (`HealthCategory.Memory`, added last in the enum). It holds `memory.commit_pct`
  (from Win32_OperatingSystem, unelevated), `memory.low_memory_events_30d` (System event 2004),
  `memory.used_pct` (sensors, admin) and `ram.total_gb`. Commit, not physical use, is what runs out.
- **The sensors check falls back to Windows' ACPI thermal zone** (`cpu.zone_temperature_c`) when the processor's own
  sensor needs PawnIO. Readings outside 35–125 °C are treated as not read (many desktops expose a fixed ~27 °C
  placeholder). If nothing is readable, an Info row says the Sensors grade covers the GPU only.
- **USB drives:** an empty reliability record, or exactly 0% wear over USB, is reported as *not read*, never graded
  (`ReliabilityCounterCheck`). A Seagate HDD in a USB caddy reports itself as an SSD with spindle speed 0.
- **Disk-error events name the drive** (`DiskEventAttribution`), separating the Windows disk from others.
  *Since 2026-10-27 (STOR-002) the value of `events.disk_errors_90d` counts only this PC's own drives,* by the
  kit's KIT-005 rule, and when that is not every error the Detail says "Counted against this PC: N of T." The kit
  reads T as the total. USB volumes' free space and encryption are information only (STOR-001, SEC-001), and the
  disk benchmark skips a drive this scan calls failing, matched by `Metric.DiskNumber` (BENCH-001).
- **"Last N days" counts state their real span (HIST-001, 2026-10-28).** `Support/RecordSpan.cs`: each event-log count,
  Defender's 90-day detections and the reliability range say "records cover only D days" when the log is shorter
  than the window. A zero from under 7 days, or from a clock that looks wrong, is information only with a Note. A log
  that covers the window changes nothing. Defender keeps detections only 15 days by default, so its "90 days" never was.
- **File History and OneDrive run without admin (BAK-001, 2026-10-28)** in `backup.files`, for the signed-in account;
  restore points and shadow copies stay in the helper (`backup.posture`). "Backed up somewhere" needs the helper's
  shadow-copy count, so `backup.files` is `RunsAfterOthers`: it runs after the helper, with `EarlierFindings`.
- **Windows 10 end of support is 2027-10-12** (consumer ESU, extended by Microsoft on 2026-06-25). It is hard-coded
  in `SysOverviewModule`, so re-check it if Microsoft moves it again. *Corrected 2026-10-27 (OS-001): that date
  is consumer ESU's, for personal use only, and was applied without checking enrolment. Every Windows end date now
  lives in `Support/WindowsSupport.cs`, with sources and a checked-on date; re-check them before each release.*
- **CPU slowdown under load (2026-10-22):** the CPU benchmark now samples `% Processor Performance` (WMI, highest
  logical processor each second) while it keeps one core busy, and reports the median as
  `bench.cpu.clock_pct_of_base`: Good ≥ 100%, Normal ≥ 85%, Bad below. It is not graded on battery. On the T470p it
  read **87%** where a healthy i7-7820HQ turbos to ~130%, which agrees with the audit's throttling finding. Verified
  with portable 7-Zip 26.05 (its output still parses). Firmware speed-limit events (Kernel-Processor-Power 37) are
  counted but shown only, because laptops log a few at start-up while healthy and there is no baseline yet.
- `build-release.ps1` wrote to the drive root under Windows PowerShell 5.1 (PKG-003). It is fixed; never use
  `$PSScriptRoot` in a `param()` default.
- Details and verification for all of the above: `docs/warning_bug_and_solutions.md` (PRIV-001, HW-001, CI-001, PKG-003).

## Suggested next session

**Daily smoke, every day (since 2026-10-29).**
- **The command.** `daily-smoke.ps1 -Push` records the row in `docs/daily-smoke.md`, commits only
  that file, and pushes it. Any machine continues the routine with `git pull`, then the same
  command.
- **The schedule.** `register-daily-smoke.ps1`, run once by the owner, makes it a Task Scheduler
  task: 09:00 daily, as soon as possible if the PC was off. Run it on **one** machine only, the
  T470p: the dev desktop's smoke never scans (DiskSpd), and two machines pushing rows the same
  morning can conflict. A rejected push is retried once after a rebase; anything else leaves the
  row committed locally, says so, and does not fail the smoke.
- **Read the newest rows** after each pull. A FAIL row means look at the logs under
  `%TEMP%\pchealth-smoke\` on the machine that ran it.

**Start here next week (state saved 2026-10-28, end of the week).** The week's summary for the owner
and the manager is in [docs/WEEKLY-REPORT-2026-10-28.md](docs/WEEKLY-REPORT-2026-10-28.md).

**Where things stand:**
- **Step B is committed and pushed, but not released.** It was built, reviewed (12 findings
  confirmed plus 1 uncertain, all fixed) and gated. The daily smoke has not been run on it; the
  gate ran the build, all tests and the kit checks.
- **The audit stick still carries Step A, `2071660`, kit `2026-10-28.1`.** Its `Results` holds 3
  pilot zips and 1 test zip.
- **The week ended because of the owner's weekly usage limit (87%).** Prefer single-agent work
  until it resets.

**In order:**
1. **The release.** `daily-smoke.ps1`, then `build-release.ps1` (after the commit, so the exe is
   stamped with HEAD).
2. **An elevated scan of the new build.** It needs the owner's UAC click. Check that every `smart.*`
   and `storage.*` drive row has the same `DiskNumber` as the `storage.disk.role` row for that
   drive. If not, a healthy laptop gets the grey "a drive did not report" note (ADV-009) and H1
   cards land on the wrong disk.
3. **The stick.** Copy the release onto it, keeping `Results`: fingerprint `Results` before and
   after, and verify every file. Record the build in NEXT-STEPS. Ask the owner first: the stick
   may be in use.
4. **The owner's checks:**
   - ACCEPTANCE K1-K12 in the window; K2 and K7 are blocking;
   - a headless scan on the Windows 11 desktop;
   - golden-image decisions A-J;
   - the commercial ESU question.
5. **Then:**
   - REP-002 (decimal commas, about 30 lines, as one class);
   - the `disk.reliability` timeout with several USB devices attached;
   - TOOL-005's smoke fix;
   - then Step C or Step D (multi-agent, after the limit resets).

**Newest (state saved 2026-10-28, evening): Phase 1 Step B is built, and not committed.**
- **What was built.** The "What you can do" section, from [docs/STEP-B-SPEC.md](docs/STEP-B-SPEC.md).
  Five work groups built it in turn:
  1. **The advice core:** the model, the catalog (`src/PcHealth.Core/Text/en.json`), the formatter,
     the engine, and the report wiring (`ScanReport.Advice`, SchemaVersion "1.1").
  2. **Two disk rows and a shared damage list.** The volume check writes two information-only rows
     per disk, `storage.disk.role` and `storage.disk.used_gb`. `DriveDamage` moved to Core, so H1
     and the benchmark gate share one list.
  3. **The items:**
     - live: H1, O4, O1, the low-space card (D4-D9), O3, and S1 with S4's label;
     - text only: M5, M6 and S4's other labels.
  4. **The surfaces:**
     - the HTML report, the headless console and the window;
     - the cap of 5, then "N more";
     - "Don't show again", in `%APPDATA%\PcHealthCheck\dismissed-advice.json`;
     - ACCEPTANCE section K.
  5. **The pilot replay** (`PilotReplayTests`) and these records.
- **The owner's defaults** (the spec's section 11):
  - H1 may be hidden, and it comes back when the drive gets worse;
  - no cards for the other "Action needed" readings;
  - Step C row 3 is not pulled in.
- **Grades, the kit's verdicts and the fleet table do not move.** A new kit check proves that the
  advice section and the disk rows change no column.
- **Proof so far:**
  - 1205 tests pass (1139 Windows and 66 Linux fixture tests), and so do the 286 kit checks. The
    Release build is clean with `-warnaserror`.
  - The healthy, unelevated and freshly reimaged fixtures give zero cards.
  - **The pilot replay** gives:
    - one H1 "Back up now" card, on the T470p's own SSD;
    - "not measured" lines on the two T470s;
    - nothing about the auditor's USB drive.
  - **A live unelevated scan of the Release exe on this T470p** (2026-10-28 18:41, built from the
    working tree):
    - 16 checks Completed, **no card**, the grey drive-health note, 10 other findings, and no item
      error;
    - disk 0 is `system`, with 118.3 GB in use; the three USB disks are `external`;
    - its commit charge is 82% (Normal) at 5.4 days up, so S1 will rightly appear once it has been
      up more than 7 days.
- **Faults logged:** ADV-001 to ADV-011 (ADV-009 to ADV-011 from the review round: 12 findings confirmed by skeptics, all fixed, plus one uncertain one fixed; a gate re-ran everything), STOR-003, ACC-001, TEST-002, and TOOL-005 to TOOL-007.
  REP-002 also gained the console's "(7,0s)".
- **Needs a person before the commit.** No agent can claim these:
  1. **The elevated scan on the T470p** (one UAC click; the spec's section 9). Every SMART and
     reliability row's `DiskNumber` must equal the role row's `DiskNumber` for the same drive.
  2. **ACCEPTANCE section K, rows K1-K12, in the window on the T470p.** K2 and K7 are blocking.
  3. **Both dev PCs scanned, one of them elevated, with no unexpected card.** This is work group
     5's "done when" and a Phase 2 gate. The dev desktop has DiskSpd, so its smoke reports
     `Scan: SKIP`. Use `--headless-scan --skip-benchmarks` there.
  4. **Before Phase 2:** one headless `--skip-benchmarks` scan on the Windows 11 dev desktop.
- **Then, in this order:**
  1. commit, with the privacy grep first;
  2. `build-release.ps1`, since the exe is stamped with HEAD;
  3. re-copy the stick only if shipping. It still carries `2071660` (Step A).
- **Next:**
  - Step D, the intake and handover check.
  - Step C's readings. Its row 3 now carries S4's hard-disk label, and row 10 carries O4's
    managed-laptop reads.
  - O3's throttle-event band waits on the plan's baseline list.

**Before that (state saved 2026-10-28, afternoon): Phase 1 Step A is built.**
- **What was built.** Rows 1-13 of the plan's Step A:
  - Windows support truth: OS-001, the ESU licence, editions, and update age counted from the
    release;
  - false alarms removed: the catalog-signed tray icon, the time service, young installs, USB
    volumes and USB disk errors, SSD temperature bands;
  - a benchmark that skips failing drives;
  - "last N days" counts that state their real span;
  - backup readings that run without admin;
  - startup checks that number accounts and never name them;
  - menu paths that work on Windows 10.
- **The kit.** The kit's `requireWindows11Ready` is off (decision 9), and the kit version is
  `2026-10-28.1`.
- **The process.** Six agents built it. A five-lens review, each lens checked by a skeptic,
  confirmed 22 faults. Four fixers and a gate closed them. The fault log has every entry (OS-001..3,
  KIT-008, TIP-001, SIG-001, START-001/2, PRIV-002/3, SEC-001/2, BENCH-001, SMART-002, STOR-001/2,
  HIST-001, BAK-001), each with a "Review round, 2026-10-28" paragraph where it applied.
- **Still open:**
  - REP-002, decimal commas in about 30 report details;
  - the elevated paths, which no live run covered: SMART temperature by drive kind, USB
    encryption rows, the helper's disk numbers and shadow-copy count, and "Account N" with two
    accounts;
  - everything on Windows 11, where nothing was run live.

  The plan's "fresh-image baseline" (VM, then a restored spare laptop) is where those get proven.
- **First elevated run of the Step A build (2026-10-28 13:51).** The release exe ran
  `--include-admin`, and the owner approved UAC. This PC had four USB volumes attached.
  - 22 of 23 modules Completed. These were as designed:
    - `smart.ssd_temperature_c` 37 °C Good, on SSD bands;
    - USB encryption and free-space rows information only;
    - `backup.any_protection` Good from the helper's shadow-copy count;
    - `startup.other_accounts` "Yours is the only user account".
  - **`disk.reliability` was PartiallyCompleted:** "Reliability counters via the storage
    association: the query timed out" (the 60 s PowerShell fallback). Step A did not change that
    query, only disk numbers and wording, so it most likely comes from the four USB devices
    attached (NVMe enclosure, flash stick, HDD caddy, Google Drive). The kit run of 2026-10-26, with
    one USB drive attached, completed it. **Not yet investigated:** check whether one USB bridge
    hangs `Get-StorageReliabilityCounter`.
  - REP-002 showed live: "About 0,8 years" in `smart.power_on_hours`.
- **Next.** Step B (the advice engine; built since, see above) and Step D (intake and handover
  check). The golden-image tasks are in [docs/GOLDEN-IMAGE-PLAN.md](docs/GOLDEN-IMAGE-PLAN.md), and
  the owner's decisions A-J gate the build.
- **The stick.** Since 2026-10-28 13:11 it carries the Step A build:
  - `1.0.0-rc.1+2071660`, kit `2026-10-28.1`;
  - `H:` on the T470p today;
  - all files verified, `Results` unchanged.

  From this build on, the fleet rows of the three pilot laptops move from REPLACE to FIX FIRST, for
  their real hardware faults. CI was green on `2071660`. The daily smoke passed on the same commit
  (781/783 + 285 kit, scan 17 done / 1 skipped).

**Before that (state saved 2026-10-27, evening): the remedies plan and the owner's decisions.**

The owner asked whether the app could do more than diagnose: clear junk when the disk is full,
calm a browser that eats the RAM, and speed up start-up. [docs/REMEDIES-PLAN.md](docs/REMEDIES-PLAN.md)
is the answer.
- **Read first:** its first section, "Update, 27 October 2026". It records the owner's decisions and
  what they change. Section 6 lists the decisions themselves. A private page of the plan exists on
  the owner's claude.ai account; the file in the repo is the source.
- **The decisions:**
  - Laptops are reimaged before handover, by restoring a Rescuezilla image.
  - Every laptop gets four apps: Chrome, Unikey, LightShot, NetBird. They are also the startup
    allowlist.
  - The app never deletes the user's or Windows' files.
  - The kit records profile sizes before the wipe, with accounts numbered, never named.
  - Testing is in a VM first, then on spare test laptops.
  - English first, then Vietnamese in the app window, its report and the kit's questions. The kit
    prints each English line followed by its Vietnamese one, and the `.ps1` files stay ASCII.
  - Which Windows goes on a reimaged laptop is **not decided**. The plan lists the facts.
- **The owner's go and later answers.** The owner said "go" on 2026-10-27, and answered the
  "Still open" questions the same evening (plan section 6, decisions 8-15). Only the commercial
  ESU purchase is still open.
- **OS-001**, found during that review, is fixed in Step A (see above). The app called an
  unenrolled Windows 10 company laptop "supported until 2027-10-12", which is the consumer ESU date.
- **Facts about the fleet that a later session needs:**
  - None of the T470/T470s/T470p processors is supported by Windows 11. The kit marked every one
    REPLACE while `requireWindows11Ready` was on. It is off since 2026-10-27 (decision 9), so that
    is now a note naming the options. Turned back on, it marks them REPLACE again, Windows 11
    installed anyway included (KIT-008).
  - The audit stick also carries the reimage images (one captured on a ThinkPad T14 Gen 1, one on
    the pilot T470p, whose SSD is now failing). It is the same 1 TB Seagate that logged disk errors.
    Error Checking passed on 2026-10-27, and the owner keeps using it. See NEXT-STEPS.
- **Where the work runs best:**
  - Windows 11 tests and the VM host: the dev desktop (Windows 11 Pro, 32 GB). The T470 laptops
    can't host a Windows 11 VM.
  - Real-scan smokes: the T470p, which has no DiskSpd.
  - Both machines continue from `git pull`. Don't work on both at once; HANDOFF and NEXT-STEPS
    conflict.
- **The privacy grep before committing** now also covers the reimage images' folder names and any
  personal backup folder names on the stick. They hold a company name and a user name, and neither
  goes into the repo.

**Before that (state saved 2026-10-27, morning):** the pilot is done and was reviewed twice. The re-review on the
T470p found a sixth fault (KIT-005), and the owner's photo of the kit found a seventh, the `ù` in every
progress line (KIT-006). The kit's admin path has run for real: 22 of 22 checks, elevated. Closing
the kit's window, Ctrl+C or an error no longer loses or misreports a laptop (KIT-007, five review
rounds and a confirmation round). The stick (`H:` on the dev desktop, `E:` on the T470p) carries
`1.0.0-rc.1+c067e3a`, kit `2026-10-26.1`. The owner is checking the rest of the fleet with it, and
must keep `Results\` on the stick until the audit is over: the summary orders each laptop's checks
by what was on the stick, not by the laptops' clocks. The pilot's fleet table is in `audit-results/` on the T470p only; git ignores it,
so another machine has to rebuild it from the zips.

When the next zips come back, the loop is the same one that worked:
1. Copy everything in the stick's `PcHealthCheck\Results\`, the `.work-*` folders included, into
   **`audit-results/`**. Git ignores that
   folder, because the zips hold serial numbers, computer names and Windows user names, and this
   repository is public. Check `git status` and see nothing.
2. Follow `audit-results/README.md`, then the review order at the top of NEXT-STEPS.
3. Build the fleet table, then read for *wrong reasons*, not just wrong verdicts. Every fault in
   the first round was a plausible-looking reason attached to a correct verdict.
4. Fix with tests and a fault-log entry, rebuild the kit **after committing**, and re-copy it to
   the stick.

**Never commit a zip, a serial number, a hardware UUID, a computer name or a user name.** This bit
the first round: a review agent copied a real serial into a test fixture, and it was caught only by
grepping the staged diff for the known serials before committing. KIT-007 nearly published the
T470p's hardware UUID the same way. Do that grep every time, with this machine's UUID in it.

**Engineering, when the fleet is not the job:** M5 remainder — Linux phase 2 (`PcHealth.Cli`, needs
the Ubuntu 26.04 VM; neither WSL nor Docker is on the dev desktop), then Vietnamese localization.
Acceptance rows still needing a person: J3's display-language half, J1, B1, B4, E1, G3, D4.

The tree is clean and pushed, and CI is green on both jobs.

**Needs a person:** row J3, the app on a Windows set to Tiếng Việt (highest-value acceptance row left); then
J1 and the other person-only rows in `NEXT-STEPS.md`; and the decisions YOU-1 to YOU-3 (public repo for signing,
version bump, fix-then-accept).

**Engineering, in order:** the P2 list in `docs/feature-gap-2026-10-21.md`:
1. ~~CPU throttling under load~~ done 2026-10-22. **Thermal sleeps: deliberately not built (2026-10-23).** Kernel-Power 42 carries a numeric `Reason`, but the T470p's own events confirm only 4 (Application API), 6 (Hibernate from Sleep, fixed timeout) and 7 (System Idle). The code for a heat-triggered sleep never occurs on a healthy machine, and Microsoft does not document it. Build this only from a real thermal event, matched by code and never by the localized message text.
2. ~~A lifetime CRC count on a first scan graded Bad~~ done 2026-10-22: 1-100 is Normal ("watch for growth"), and growth between scans is still Bad through TrendAnalyzer.
3. ~~Battery identity, AC/DC time, usage-aware wording~~ done 2026-10-22 (`battery.identity`, `battery.time_on_battery_pct`, capacity Detail). Pack age done the same day: `battery.age_years` from SMBIOS type 22 (the SBDS date word, unelevated), plus a fade rate and a dated linear projection in the capacity Detail ("about 13.2% a year, 40% around 2027-07" on the T470p, which matches the audit). Charge state done 2026-10-23 (`battery.charge_state`, Info, root\wmi BatteryStatus, unelevated).
4. ~~SMART spare blocks~~ done 2026-10-22 in the vendor-neutral form: `smart.attributes_at_threshold` compares every attribute with the failure limit its maker set (MSStorageDriver_FailurePredictThresholds), now and at its worst. **Deliberately not done:** reading 171/172/183/241 by number, because their meaning differs by vendor (183 is link downshifts on Intel, runtime bad blocks on Samsung), so it would raise false alarms on other brands. Live check pending: the elevated scan timed out waiting for UAC.
5. ~~Top memory and CPU users~~ done 2026-10-22: `memory.top_users` and `cpu.top_users` in System overview, top five by name only, summed per program. CPU is time since each program started, not a sample, so it doesn't measure the scan itself. Unelevated, protected and other-account processes can't be read (the Detail says how many). PRIVACY.md, USER-GUIDE.md and collect-results.ps1 list them as report contents (rule 9).

After those:
- ~~start the elevated helper alongside the local modules~~ done 2026-10-23 (rule 11)
- ~~speed up `startup`~~ done 2026-10-23: 11 s → ~5 s on the T470p. Files are examined 4 at a time, each distinct file once, and disabled items are listed but not examined (nothing reads their signature or hash).
- ~~move the CI actions off Node 20~~ done 2026-10-23: checkout@v7, setup-dotnet@v6, upload-artifact@v7, all on Node 24, with the inputs checked against each `action.yml`
- the Linux provider: **phase 1 done 2026-10-23**, unelevated only, and verified on a real kernel the same day (CI's `linux` job; details in NEXT-STEPS M5-LNX). Next is phase 2 (`PcHealth.Cli`) per NEXT-STEPS (M5-LNX). It needs the Ubuntu 26.04 VM, since this laptop has no WSL distribution and no Docker.

**Every day:** run `daily-smoke.ps1` ("do the daily smokes").
