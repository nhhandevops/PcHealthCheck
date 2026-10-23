# Next steps — who does what

Written 2026-08-26, after a six-way audit of `ACCEPTANCE.md`, `RELEASING.md`, `HANDOFF.md`, the
codebase, this machine, and the project's own recurring defect class. 148 candidate tasks were
found and then deduplicated and corrected by an adversarial pass; what survives is below.

Two lists: **what needs a person** (you), and **what does not** (me). The split is by whether a
task genuinely requires a human at the keyboard — a UAC prompt, eyes on a window, a decision only
the owner can make, hardware to unplug — not by difficulty.

---

## Where this actually stands — 2026-08-26, later the same day

**The goal changed: nothing is being published.** The app is being carried to different machines
to see whether it behaves, and the results are being collected and read back. Everything about
making the repository public, SignPath code signing, the GitHub release, the winget manifest and
the SmartScreen submission is **deferred** — including the Mono.Posix licence question, which only
matters at publication. `RELEASING.md` keeps those steps for when that changes.

What replaced them: `collect-results.ps1` (runs a scan and packs one zip to send back) and
`TRYING-IT-OUT.md` (what to do on each machine, and which machines are worth finding). Both ship
inside the release zip, because a collector left behind in the repo is one that never gets used.

**Done since the list below was written:**

| | |
|---|---|
| ENG-1 structural fix | ✅ `ModuleFailures` — recording a failure and degrading the status are one call |
| ENG-2 the 16 modules | ✅ fixed, then reviewed, then corrected: the review found 2 blockers and 5 regressions in the fixes themselves |
| ENG-3 headless defect #5 | ✅ and the GUI's inline copy now routes through the same helper |
| ENG-4 exit codes | ✅ documented; a Bad health grade is deliberately still exit 0 |
| ENG-5 tests | ✅ `ModuleFailuresTests` locks the contract in both directions |
| ENG-6 benchmark consent | ✅ derived from the constants, `BenchmarkConsentTests` ties them together |
| ENG-7 third-party notices | ✅ every licence read from package metadata; one flagged as unresolved |
| ENG-10 / C3 🔴 | ✅ passed — no file on any drive changed |
| ENG-11 I/O baseline | ✅ recorded before DiskSpd exists |
| ENG-9 single-file wording | ✅ 2026-10-23: managed code is bundled, natives deliberately are not. After repeated runs of the release exe no `%TEMP%\.net\PcHealthCheck` exists (HrSync.App, built the other way, has one), and `build-release.ps1` now fails if the natives ever end up bundled |
| ENG-12 / C1 🔴 | ✅ 2026-10-23, release build `+ddad8bd`: 0 TCP/UDP sockets owned by the app or any descendant (7z.exe, conhost.exe seen) across 93 samples of a 91 s scan; the report states 0. Limit: about 1 s per sample, so a child living under a second could be missed. Strictly closing that needs Filtering Platform auditing, an admin system-setting change |
| ENG-15 J3 culture half 🔴 | ✅ 2026-10-23: vi-VN regional format, elevated, readings identical to en-US (see J3) |
| ENG-16 artifact rows | ✅ 2026-10-23 on `+16d1fcf`: A1, A2, A5, A6, C2, C4, C6, C7 and F1 pass; A4, F3 and J2 pass their scriptable half. A5 failed first (the report dropped the commit) and was fixed. See `ACCEPTANCE-RUN-2026-10-23.md` |
| Tests | 291 → **305**, all passing |

**The review pass earned its keep, and this is the part worth remembering.** The first round of
fixes compiled, passed 299 tests, and produced an identical scan on this laptop — and still
contained two blockers. Both were over-corrections: making a *deliberate* non-check degrade, which
fires the amber banner on ordinary healthy machines. An empirical scan on one machine in one
configuration cannot see that; only reading the code against other configurations can.

- `StorageReliabilityModule` degraded on any volume with no dirty bit. Verified by running
  `fsutil dirty query` on this laptop's FAT32 Google Drive mounts: `Incorrect function` — the
  filesystem refusing, which elevation cannot fix. Every elevated scan on any PC with a cloud
  mount or an exFAT data drive would have raised the banner.
- `SensorsModule` degraded whenever a CPU temperature sensor read back empty. A reviewer wrote a
  throwaway console app against LibreHardwareMonitorLib 0.9.6 on this exact laptop and measured
  it: eleven CPU temperature sensors, **every value null**, on a machine where nothing is wrong.
  The library activates those sensors from CPUID; only their values need the driver. The gate is
  now whether the PawnIO driver is actually *running*.

**Still open, deliberately not done:** 14 minor findings from the second review (metrics asserted
from failed reads in paths nobody was assigned, `TimeSyncModule` never converted to
`ModuleFailures`, note-ordering that makes the GUI name the wrong reason first). None of them
falsely reassures on a healthy machine, which is why they waited. `BrowserExtensionsModule.cs`
also carried a literal NUL byte at offset 8349 (fixed 2026-10-23: written as `"\0"`, same string, and git diffs it as text again) — pre-existing at HEAD, harmless to the build, but
it makes git treat the file as binary and lose `diff=csharp`.

**Nothing is committed yet.** Worth fixing before collecting results from several machines: the
build stamps `1.0.0-rc.1+<commit>` into every report, and that commit is currently the last one,
not this code — so results from different machines would not be traceable to what produced them.

---

## The headline

**The app has 16 more instances of the defect class it has already shipped four times**:
*presenting an unchecked result as a clean one*. They were found by reading every module against
rules 7 and 8 in `HANDOFF.md`, and two were verified by hand before writing this down.

The two worst:

- **`BatteryModule.cs:31-37`** — when the WMI battery query *fails*, the list comes back empty,
  and the module tells the user **"No battery detected — desktop system, module skipped."** On a
  laptop. A failure is reported as a fact about the hardware.
- **`StartupModule.cs:37-51`** — if the Winlogon registry key cannot be opened, `shell` and
  `userinit` are both null, `IsNullOrEmpty(null)` is true for both, and the module emits
  `startup.winlogon_default = true` with the text **"Shell and Userinit have their default
  values."** This is the *security* check for a known malware persistence point, asserting clean
  from a read that returned nothing.

There is a **structural** cause under all 16, which is why they keep coming back: every module
hand-rolls its own "did anything fail?" bookkeeping, and the ones that get it right do so by
maintaining a `failures` counter that is easy to forget. Fixing them one at a time is what the
2026-08-22 audit did, and four months later there are sixteen more. See `ENG-1` below.

---

## Your list — needs a person

Do them in this order; the ordering is load-bearing where noted. **Tell me the result of each**
and I will record it in the acceptance run document.

### 1. Before anything else — three questions only you can answer

| # | Decision | Why it cannot wait |
|---|---|---|
| **YOU-1** | **Does this repository become public?** | SignPath Foundation code signing is free but **requires a public repo**, and approval takes weeks. Everything about a signed release is blocked behind this. Read `PRIVACY.md` and the commit history first — the whole history becomes public too. If the answer is no, the release ships unsigned and every user meets a SmartScreen warning. **Decided 2026-10-23 by the owner: yes, public, with a clear "not fully tested, bugs may occur" note** (README.md, which also ships in the zip). Before the switch: all 59 commits were scanned for secrets and key files (none), the Mono.Posix licence was traced to MIT (THIRD-PARTY-NOTICES), and the author e-mail on every commit was flagged to the owner. |
| **YOU-2** | Ship as `1.0.0-rc.1` or bump to `-rc.2`? | The zip has been rebuilt twice since the tag name was chosen, and the published artifact will not match any earlier `rc.1` build. My recommendation: **bump to `-rc.2`**, because a version that identifies two different binaries defeats the point of printing it in the report. |
| **YOU-3** | Fix the 16 defects before or after acceptance? | My recommendation: **before**. They change module statuses, which changes what the amber "some checks could not finish" box does, which is row E3 — blocking. Running acceptance first means running much of it twice. |

### 2. Rows only a person can run

Each of these needs your hands. Where I can prepare or verify around your click, I have said so.

**Consent screen (section B) — all seven untouched by both runs.**

| Row | What to do | What to report back |
|---|---|---|
| **B1** 🔴 | Delete `%APPDATA%\PcHealthCheck` **and** `%LOCALAPPDATA%\PcHealthCheck` first, then launch. | Is every checkbox unticked, and is Scan disabled until you tick one? |
| B2, B3 | Read the consent list and the "Needs administrator" heading. | Does each row say what it reads/writes? Is the heading noticeable now? (It was restyled after run-1 feedback and nobody has looked since.) |
| **B4** 🔴 | Tick only non-admin checks, scan. | Any UAC prompt at all? There must be none. |
| B5, B6, B7 | Tick an admin check, scan, accept the prompt. Then close, reopen. | Exactly **one** prompt? Are your choices remembered? |

**The honesty rows (section E) — the ones this project exists for.**

| Row | What to do | What to report back |
|---|---|---|
| **E1** 🔴 | Tick an admin check, scan, then **decline** the UAC prompt. | Does the amber box appear, naming each check that could not finish? Is the headline marked **INCOMPLETE**? *This is where defect #5 was found by you, by hand, last time.* |
| **E2** | Run "Startup for other accounts" elevated. | **Newly testable — I was wrong to write this off.** `CodexSandboxOffline` exists on this machine and is signed out, which is exactly E2's precondition. Does it say the registry could not be read, rather than claiming nothing is there? |

**Optional tools (section H).** Note the banner offers **PawnIO first** and will not advance past
it, so H2 means installing PawnIO — a signed kernel driver. Decide if you are comfortable with
that; H6 removes it again.

| Row | What to do | What to report back |
|---|---|---|
| H1 | Read the banner. | Does it name the tool, why it helps, and the exact command? |
| H2 | Accept the install. | Exactly one UAC prompt? Does CPU temperature appear next scan? |
| H3, H4 | Close the app. Choose "Keep it". Reopen, close again. | Is removal offered with a visible command? Offered *again* after "Keep it", with nothing removed? |
| **H5** 🔴 | After I install DiskSpd from a terminal, close the app. | Is DiskSpd **never** offered for removal? It was not installed by the app, so it must be structurally unremovable. |
| H6 | Accept a removal, read `dependency-history.txt`. | Is the reinstall command logged? |

**Stress and hardware (sections D, I).**

| Row | What to do | What to report back |
|---|---|---|
| **D4** 🔴 | Start a scan with Disk speed ticked, wait until `D:\PcHealthCheck-bench\` appears, press **Cancel**. | Is the folder and the `bench-<pid>.tmp` gone afterwards? Say when you click and I will snapshot the filesystem either side. |
| I1 | Cancel a scan mid-way. | Does it stop promptly and say nothing was changed? |
| **I5** 🔴 | **Needs hardware you may not have**: unplug an external drive *during* a benchmark of it. | This machine has no external drive. If you have a USB SSD/HDD, this is the only way to run it; otherwise it must be recorded as untested and the sign-off cannot claim 19/19. |
| I4 | Select an external drive, unplug it, scan. | Reported as unavailable rather than failing the scan? |

**On-demand actions (section G).** G4–G7 need **your own VirusTotal API key**.

| Row | What to report back |
|---|---|
| G1, G2 | Does the Quick scan consent state duration, that Defender may contact its cloud, and what happens on a find? |
| **G3** 🔴 | Run "Scan a folder" on a folder you choose — is **nothing** in it moved, renamed or deleted? |
| G5, **G6** 🔴, G7 | With a key: does the consent list every fingerprint? Declining sends nothing? Accepting contacts only virustotal.com? |

**Elsewhere (sections A, J).**

| Row | What to do |
|---|---|
| A3 | Download the published release in a browser and run it — SmartScreen only appears on a file with Mark-of-the-Web. Blocked until the release is published. |
| A7 | Resize the window small and large. Anything clipped or overlapping? Three such faults were fixed from screenshots in August; a fourth is plausible. |
| **J1** 🔴 | **Possibly recoverable without a second machine.** This is Windows 10 **Pro** with the hypervisor present, so **Windows Sandbox** gives a clean VM with no .NET SDK. Run elevated: `Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM` — tell me the State and I will handle the rest. |
| **J3** 🔴 | Switch display language to Tiếng Việt, sign out and back in, run a full scan. **The single highest-value row available.** This project has already shipped two locale bugs, and a decimal-comma Windows is where a speed or a temperature silently comes out 1000× wrong. I can test the *culture* without you (`ENG-J3` below) but only you can change the display language. **2026-10-23, first half PASSED:** with the regional format set to Vietnamese (decimal comma), an elevated scan of the T470p (`+ddad8bd`) gave readings identical to an English scan the same morning, with all 24 modules completed and spot checks correct (battery 50%, SSD 23%, CRC 25, TRIM on). **Second half still open:** the display language stayed English (no Vietnamese text in the report, language list "en-US, vi"), so Windows tools still answered in English. Re-run with Windows display language set to Tiếng Việt. **Deferred by the owner on 2026-10-23: "not needed right now".** |
| J4 | Run as a Windows user with no administrator rights. |

### 3. At the end

| # | Task |
|---|---|
| YOU-4 | Fill in and sign the sign-off block in `ACCEPTANCE.md`. **If I5 could not be run, the block must say so** — "19/19 blocking rows passed" would be false, and an unrecorded known gap becomes a surprise for whoever uses this next. |
| YOU-5 | If YOU-1 is yes: apply to SignPath (<https://signpath.org/apply>), flip repo visibility, create the GitHub release. |

---

## My list — no person needed

Ordered. Items marked ⚠ have a dependency that must be respected.

### Priority 1 — the defect class, before acceptance runs again

| # | Task |
|---|---|
| **ENG-1** | **Fix the structural cause first.** Add a `WindowsModuleBase.RecordFailure` helper and a `Wmi.Query(..., notes, ref failures)` overload so "a query failed" is recorded in one place instead of hand-rolled per module. Without this, the 16 fixes below are the same patch sixteen times and a seventeenth appears next quarter. |
| **ENG-2** | Fix all 16 modules: `BatteryModule` (failure reported as "desktop system"), `StartupModule` (unreadable Winlogon reported as default), `DiskSmartModule`, `StorageReliabilityModule`, `BackupPostureModule`, `SysOverviewModule`, `EventsModule`, `UpdatesModule` (QFE error bound to `out var` and never read), `SecurityPlatformModule` (Device Guard error discarded with `out _`), `SecurityPostureModule`, `CrashDumpModule`, `SensorsModule`, `BrowserExtensionsModule`, `NetworkHealthModule`, `CpuBenchmarkModule`, and `ElevatedWorkerSession` (reports `Completed` when the pipe dies before `ScanCompletedMessage`). |
| **ENG-3** | **`HeadlessScan.cs:71-73` never synthesizes `Failed` for admin modules that did not run.** Defect #5 — the declined UAC prompt showing GOOD — was fixed in `MainWindowViewModel.cs:224-233` **only**. The headless path still has the original bug. |
| **ENG-4** | `HeadlessScan.cs:116` returns 0 unconditionally. A scan that graded Bad, had four modules partially complete, or had its UAC prompt declined all exit 0 — so no script can detect a failed scan. |
| **ENG-5** | Write the tests. **No module has a unit test today**: all 19 test files target helpers. Per rule 8, logic that only runs on an unhealthy PC needs tests because a dev machine never executes it — and every one of the 16 above is exactly that logic. |

### Priority 2 — defects found today

| # | Task |
|---|---|
| **ENG-6** | **The benchmark's consent text is false.** `DiskSpeedModule.cs:39` promises `<drive>:\PcHealthCheck-bench\bench.tmp`; the code writes `bench-<pid>.tmp` (`BenchmarkGate.TestFileNameFor`). The I3 fix renamed the file and left the disclosure behind. This is the only check that writes to your drives, and its consent statement is wrong. Add a test asserting the two agree. |
| **ENG-7** | **`THIRD-PARTY-NOTICES.md` is materially incomplete and it ships inside the zip.** It lists four NuGet packages and omits every native library that physically ships: `libSkiaSharp.dll`, `av_libglesv2.dll`, `e_sqlite3.dll`, `libHarfBuzzSharp.dll`, `libMonoPosixHelper.dll`, `MonoPosixHelper.dll`. That is a licensing problem in a file whose only job is licensing. |
| **ENG-8** | `HANDOFF.md` says "CI has never run yet — it activates on first push". **It has run 20+ times, all green**, most recently on `75a4efe`. Correct the doc. |
| **ENG-9** | Reconcile the single-file packaging note. `build-release.ps1` and `.github/workflows/build.yml` both pass `PublishSingleFile=true` while the script header says "It is NOT a single file". The *outcome* is right (natives sit beside the exe, no temp extraction), but the text reads as a contradiction — and prove the no-temp-write claim rather than asserting it. |

### Priority 3 — acceptance rows I can run

⚠ **Ordering matters:** `ACC-C3` and the disk-I/O baseline must run **before** DiskSpd is
installed, because headless mode runs every non-admin module and cannot deselect the benchmark.

| # | Task |
|---|---|
| **ENG-10** ⚠ | **C3** 🔴 — snapshot every drive root and Documents before and after a scan, confirm no difference. Must run before DiskSpd is installed. Watch G:/I: specifically. |
| **ENG-11** ⚠ | Record the disk-I/O-error baseline before the benchmark exists, so the benchmark cannot be blamed for or credited with a change. |
| **ENG-12** | **C1** 🔴 — re-measure zero network connections by sampling `Get-NetTCPConnection`/`Get-NetUDPEndpoint` on the live PID, including DiskSpd child processes. Today's evidence is structural; run 1's socket sampling was against a superseded binary. |
| ENG-13 | Install DiskSpd from a terminal (`winget install Microsoft.DiskSpd`, no UAC). Doubles as the precondition for your **H5**. |
| ENG-14 | **D2, D3** 🔴, **D5–D8** — run the benchmark, poll the folder's lifetime, confirm nothing is left behind. Note the file goes to **D:** (20.9 GB free), not C: — the selector picks the roomiest volume per physical disk. |
| **ENG-15** | **J3 culture half** 🔴 — run the app under `vi-VN` culture and diff every number against the `en-US` run. Catches decimal-comma parsing without needing your display language changed. Your J3 still needed for the real thing. |
| ENG-16 | Re-run the artifact rows (**A1, A2, A4, A5, A6** 🔴, **C2** 🔴, **C6** 🔴, C4, C7, F1, F3, J2) against whatever build ships — every one was verified against a binary that has since been superseded. |

### Priority 4 — release mechanics

| # | Task |
|---|---|
| REL-1 | Commit and push today's work (checksum fix, acceptance record, `docs/warning_bug_and_solutions.md`, this file). CI will pass — nothing compiled changed. |
| REL-2 | Draft `release-notes.md`, which `RELEASING.md` already references but does not exist. |
| REL-3 | Prepare the winget manifest. |
| REL-4 | After YOU-2: bump the version, rebuild, re-record hashes. |

### Deferred — real work, but not before a release

| # | Task |
|---|---|
| M5-VI | Vietnamese localization. Strings are hard-coded across XAML, module notes, `src/PcHealth.Core/Rules/*.json` tips and the report exporters — this is a large, structural job, not a translation pass. |
| M5-LNX | Linux provider. **Decided 2026-10-23 by the owner: unelevated-only first.** Checks that need root (SMART, full system logs) are reported as "not checked, needs root", never as healthy (rule 7); a `pkexec` helper may follow later. **Phase 1** (buildable and testable on Windows CI): `PcHealth.Modules.Linux` (net10.0) with `LinuxModuleProvider`, and modules reading only `/proc` and `/sys` (battery from `/sys/class/power_supply`, memory and commit from `/proc/meminfo`, temperatures from `/sys/class/thermal` and `hwmon`, uptime, OS from `/etc/os-release`, disk space). Pure parsers with fixture-text tests (rule 8). **Phase 2:** a cross-platform headless host (`PcHealth.Cli`, net10.0: orchestrator plus exporters), so `--headless-scan` runs on Linux without the Windows-only App; verified in the Ubuntu 26.04 VM (`vm/bootstrap-pchealthcheck-ubuntu-26.04.sh` in the backup). **Phase 3:** the Avalonia GUI on Linux, then optionally pkexec. Locale: `/proc` and `/sys` are not localized; any command runs with `LC_ALL=C`. |
| ENG-REG | `RegLoadAppKey` to read a signed-out account's `NTUSER.DAT`. Deliberately deferred — it is the first time the app would touch another user's registry file. |

**Not tasks — decided already.** ClamAV was dropped on 2026-08-20 for two recorded reasons; do
not re-schedule it. The marker sweep found **zero** TODO/FIXME/HACK/`NotImplementedException` in
the entire codebase — that search does not need running again.

---

## Corrections to what was said earlier

Recorded because getting these wrong shaped advice that was given.

1. **"This machine has one drive."** One *physical* disk, but **four** lettered volumes: C: and
   D: are both partitions of it, and **G: and I: are Google Drive mounts reporting themselves as
   `DriveType 3`, local fixed disks**. The benchmark is safe from them — they appear in
   `Win32_LogicalDisk` but not in `MSFT_Volume`, and `MSFT_Partition` maps only C: and D: to disk
   0, so `DiskSpeedModule.cs:288` drops them. Verified by query, not assumed. It also means the
   benchmark writes to **D:**, not C:.
2. **"No second account."** `CodexSandboxOffline` exists and is signed out. Row **E2** is
   testable here and was wrongly written off.
3. **"J1 is lost on this machine."** Possibly recoverable via Windows Sandbox — Win10 Pro with
   the hypervisor present. Needs one elevated command to confirm.
4. **"CI has never run."** From `HANDOFF.md`, now stale: it has run 20+ times, all green.
