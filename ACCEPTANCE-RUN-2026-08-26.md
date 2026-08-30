# Acceptance run — 2026-08-26

Second pass against `ACCEPTANCE.md`, on a different machine from the one that built the first
release. Recorded because the previous run's rows were passed on a Windows 11 desktop, and the
row most likely to catch a packaging fault is the one run somewhere else.

| Field | Value |
|---|---|
| Build | 1.0.0-rc.1 — rebuilt from `75a4efe` plus the checksum fix below |
| SHA-256 of the zip | `1E5A5E43667C1B588B8F8A78AFF97587C7A3192647082BD67F6E75BB48B6FEE5` |
| SHA-256 of the exe | `F947B069FDD2B2FA61B6B1CA85164C68E74115BC54F74229E3A5A35D00FC4F07` |
| Machine | **Windows 10** Pro 19045, Lenovo ThinkPad 20J7S08T03, i7-7820HQ (4c/8t), 15.8 GB |
| Drives | One physical disk — INTEL SSDSC2BF240A4L, 224 GB SATA SSD — carrying **two** lettered volumes: C: (115.5 GB, 10.5 free) and D: (107.4 GB, 20.9 free). Plus G: and I:, which are **Google Drive mounts reporting themselves as DriveType 3, local fixed disks** |
| Second account | **Yes** — `CodexSandboxOffline`, SID `…-1006`, profile on disk, **not signed in** (`Loaded=False`) |
| Display language | English (vi-VN also installed in MUI) |
| Tester | Automated where possible; rows needing a person listed at the end |

**Why this machine matters.** Every axis differs from the first run's PC: Windows 10 rather than
11, a laptop rather than a desktop, a single SATA SSD rather than four mixed drives, and an
Intel i7-7820HQ rather than an i5-12400F. Section J exists for exactly this, and three of its
rows became testable simply by running here.

---

## Outcome

**One defect found and fixed** (`A1`, blocking). The build was then remade and every row below
was re-run against the fixed artifact. No other row failed.

| | |
|---|---|
| Rows verified this run | 17 |
| Rows still needing a person | 14 (unchanged from the first run, plus the ones below) |
| Rows not testable here | 8 — see "Not testable on this machine" |
| Defects found | 1 — fixed, and the build now fails rather than shipping it again |
| Tests | 291, all passing on .NET 10.0.400 in both Debug and Release |

---

## Failed, and fixed

### A1 🔴 — `sha256sum -c` rejected every line of `SHA256SUMS.txt`

The hashes were correct. The file was unreadable anyway:

```text
sha256sum: 'PcHealthCheck-1.0.0-rc.1-win-x64.zip'$'\r': No such file or directory
PcHealthCheck-1.0.0-rc.1-win-x64.zip: FAILED open or read
```

`Set-Content -Encoding ASCII` writes **CRLF** on Windows PowerShell 5.1. GNU `sha256sum -c`
takes everything after the two-space separator as the filename, carriage return included, and
so looks for a file whose name ends in `\r`. Exit code 1. To someone verifying a download, that
is indistinguishable from tampering.

**This is the second form of the same defect.** The first acceptance run found a byte-order
mark breaking the first line and fixed it by switching the encoding — which removed the BOM and
left the line endings, so the file went from *first line unreadable* to *every line unreadable*.
Both times the hashes were right, and both times the row was checked by comparing hashes by eye
rather than by running the tool. Comparing by eye cannot fail the way the tool fails.

**Fixed** three ways, in `build-release.ps1`:

1. The file is written with `[IO.File]::WriteAllText` and `` "`n" `` — LF, no BOM — instead of
   asking a cmdlet that gets both wrong by default.
2. The build **verifies what it just wrote**: it rejects a leading BOM, rejects any `0x0D`
   byte, and runs `sha256sum -c` for real, throwing on a non-zero exit. Git for Windows ships
   `sha256sum` under `usr\bin`, which is not on PowerShell's `PATH`, so the script looks there
   explicitly — otherwise the check would quietly downgrade to the byte heuristics on every
   machine that has the tool sitting right there, and a warning printed by every build is a
   warning nobody reads.
3. The `PcHealthCheck.exe` line was removed from the file. The exe lives inside the archive, so
   that line could never verify from the directory the checksum file sits in: `sha256sum -c`
   reported `FAILED` and exited 1 on a release where nothing was wrong. Its hash is still
   printed at the end of the build, for the release notes and the winget manifest.

Verified after the fix — `sha256sum -c SHA256SUMS.txt` → `OK`, exit 0; the file is 71 bytes of
ASCII ending in a single `\n`; and the build's own check now prints
`verified by C:\Program Files\Git\usr\bin\sha256sum.exe`.

Written up as PKG-002 in `docs/warning_bug_and_solutions.md`.

---

## Passed

**A — Install and start.**

- **A1 🔴** — after the fix: `sha256sum -c` returns `OK` and exit 0.
- **A2** — zip extracts to 11 files, no warnings. The extracted exe hashes to
  `F947B069…`, matching what the build reported.
- **A4** — `ProductName` "PC Health Check", `ProductVersion` `1.0.0-rc.1+75a4efe…` (the commit
  is embedded, so any finding traces to exact source), `FileVersion` `1.0.0.0`, Company and
  Copyright both populated.
- **A5** — the report prints `1.0.0-rc.1`, pre-release suffix intact.
- **A6 🔴** — a full non-admin scan raised no UAC prompt.

**C — The promises about your data.**

- **C1** — the report states "Network connections made by PcHealthCheck during this scan: 0".
  Verified structurally rather than by sampling sockets: the privacy gate proves no `System.Net`
  reference exists outside `PcHealth.Net.Optional`, and no module in this scan touches it. The
  first run's 165-sample TCP/UDP measurement remains the stronger evidence; this run did not
  repeat it.
- **C2** — receipt lists 3 external processes (`fsutil`, `powercfg`, `w32tm`), each with its
  full command line and exit code, and 1 file write (`battery-report.xml`, 19,600 bytes)
  marked `Deleted: true`. The temp folder was then confirmed **empty on disk** — the receipt's
  claim and the filesystem agree.
- **C4** — only `%LOCALAPPDATA%\PcHealthCheck` was created. `%APPDATA%\PcHealthCheck` was not,
  since a headless scan asks for no consent to store.
- **C6 🔴** — report read for secrets. Three hits, all benign: "password" appears once in advice
  text about password managers, and "Product key" appears as an Info row reading *"An OEM
  product key is stored in the firmware"* with the value shown as `—`. It reports that a key
  exists; it does not read it.
- **C7** — no `http`, no `https`, no `<script>` anywhere in the exported HTML. Renders offline.

**E — Honesty when something cannot be checked.**

- **E3 🔴** — on this run nothing failed, and the amber "some checks could not finish" box did
  **not** appear. Notably two modules were `Skipped`, which correctly did not trigger it: a
  deliberate skip is not a broken check, and conflating them is what makes the warning stop
  meaning anything.
- **E4** — both skipped modules gave a reason, and a genuinely useful one. `bench.disk`: *"DiskSpd
  is not installed, so no speeds were measured… the app offers to install it with `winget install
  Microsoft.DiskSpd`"*. `bench.cpu` names 7-Zip, its install command, **and** the no-install
  alternative of putting `7za.exe` on PATH.

**F — Trends.**

- **F1** — first scan on a machine with no history; reported as such, nothing compared.
- **F3 🔴** — `--no-history` wrote nothing at all: after the scan, the app's LocalAppData folder
  contained **zero files**.

**J — Somewhere other than the machine that built it.**

- **J2** — a laptop, so the battery check actually ran rather than reporting Skipped. It found
  capacity at **50% of design** (23,780 of 47,520 mWh), graded Bad, plus charge cycles 116 as
  InfoOnly. This is the first run where the battery path executed at all.

---

## Not testable on this machine

Recorded rather than left blank, because a blank row reads as one nobody looked at.

| Row | Why not |
|---|---|
| A3 | SmartScreen needs a genuinely downloaded file with a Mark-of-the-Web. A locally built exe has none. |
| **J1 🔴** | Requires a PC with **no .NET SDK**. This machine qualified until the SDK was installed here to build the release. Recorded as a loss, not passed — but **possibly recoverable without a second machine**: this is Windows 10 **Pro** with the hypervisor present, so Windows Sandbox gives a clean VM with no SDK. Confirming the feature is available needs an elevated `Get-WindowsOptionalFeature`. |
| D1–D8 | The disk benchmark needs DiskSpd, which is not installed here. The check correctly skipped with a reason (row E4), but its own rows went unexercised. |
| E2 | ~~No second account~~ — **corrected: there is one.** `CodexSandboxOffline` exists and is signed out, which is exactly E2's precondition. This row is testable here and was wrongly written off. |
| G1–G7 | Defender scan consent wording and VirusTotal both need a person; VirusTotal needs a key. |
| H1–H6 | Optional-tool install and removal need a UAC prompt and a person. |
| I1–I5 | Cancel, two copies, and unplugging drives — the last two need the benchmark, and there is only one drive here. |
| J3 🔴 | vi-VN is installed in the MUI but the display language was not switched for this run. **This is now cheap to do on this machine** and is the single most valuable outstanding row. |

---

## A real finding about this machine, not the app

The laptop is genuinely unwell, and the report reads coherently rather than as a list:

| Finding | Value |
|---|---|
| Battery capacity vs design | **50%** — replacement territory |
| Unexpected shutdowns (90 days) | **21** |
| Disk I/O errors (90 days) | **55** |
| Devices with driver problems | 3 |
| GPU driver resets (90 days) | 3 |
| Updates waiting to install | 12 |
| Free space | 9.3% on one volume |

The battery result and the 21 unexpected shutdowns corroborate each other, and the app's own tip
for unexpected shutdowns says *"Laptops: check the battery health finding."* — which is the
report doing the job it was built for. The 55 disk I/O errors are worth noting for a different
reason: **DiskSpd is not installed here, so the benchmark never ran.** Unlike the first run's
G: drive, this app cannot have caused those errors.

---

## Before the next run

1. **J3** — switch the display language to Vietnamese and re-run. Cheapest high-value row
   available; this project has already shipped two locale bugs, and a decimal-comma Windows is
   where a speed or a temperature comes out 1000× wrong.
2. **J1** — needs a machine that has never had the .NET SDK. No longer available here.
3. **D rows** — install DiskSpd (`winget install Microsoft.DiskSpd`, no UAC needed) to exercise
   the benchmark and its cleanup. Note this machine has one drive with ~11 GB free, so D3
   ("no leftover file") matters more here than it did on the first run's roomier PC.
4. Work the person-required table from `ACCEPTANCE-RUN-2026-08-22.md`, which is unchanged.
