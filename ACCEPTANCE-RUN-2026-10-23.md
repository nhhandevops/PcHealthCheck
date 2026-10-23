# Acceptance run — 2026-10-23 (ENG-16, plus C1, ENG-9 and J3's first half)

**Build under test:** the release zip `PcHealthCheck-1.0.0-rc.1-win-x64.zip`, version
`1.0.0-rc.1+16d1fcf2751fa3ae4cc8ab31d39310460802c9e9`, freshly extracted into a folder that had
never held the app. **Machine:** Lenovo ThinkPad T470p (i7-7820HQ, 16 GB, Windows 10 Pro 22H2), on
mains power. **Method:** a script (`eng16.ps1`, in that day's session scratchpad), so every row
below is reproducible. Scans sampled the app's whole process tree every ~150 ms.

## Results

| Row | Result | Evidence |
|---|---|---|
| A1 | ✅ PASS | Zip SHA-256 equals the line in `SHA256SUMS.txt` |
| A2 | ✅ PASS | 13 files, no warnings |
| A4 | ⚠ PARTIAL | ProductName, ProductVersion, CompanyName, LegalCopyright and FileDescription all filled; `ApplicationIcon` set. The taskbar icon needs a person |
| A5 | ✅ PASS (after a fix) | First run **failed**: the report said `1.0.0-rc.1` while the exe said `1.0.0-rc.1+25a2cd5…`, because the commit was deliberately cut off. Fixed in `16d1fcf` (`ScanOrchestrator.DescribeVersion`); the re-run matches exactly |
| A6 🔴 | ✅ PASS | Standard scan: no `consent.exe`, no elevated helper, `RanElevated` false |
| C1 🔴 | ✅ PASS | 0 TCP/UDP sockets owned by the app or any descendant across a 91 s scan (see NEXT-STEPS ENG-12, including its sampling limit) |
| C2 🔴 | ✅ PASS | Receipt lists every process run with its full command line; children observed (7z, fsutil, powercfg) all appear in it; it states 0 network connections |
| C4 | ✅ PASS | Only `%LOCALAPPDATA%\PcHealthCheck` (history.db; temp battery report deleted after use). `%APPDATA%\PcHealthCheck` is created only by the window's consent store. No `%TEMP%\.net\PcHealthCheck` |
| C6 🔴 | ✅ PASS | Pattern scan (product keys, BitLocker keys, passwords: 0) **and** a full read of the report text. Contents are the categories PRIVACY.md lists: PC name, drives, startup programs with paths, extensions, DNS servers, top programs by name. The firmware product key is reported as present, never read |
| C7 | ✅ PASS | No external `src`/`href`/`@import`/`url()` |
| F1 | ✅ PASS | With `history.db` set aside, HTML and console both say it is the first recorded scan; `history.db` was restored byte-identical afterwards |
| F3 | ⚠ PARTIAL | Headless `--no-history`: history.db unchanged, report says "not added to the history". The window's "Remember results" checkbox needs a person |
| J2 | ⚠ PARTIAL | Laptop: battery module Completed, capacity 50 % graded Bad. The desktop half ("Skipped, no battery") needs a desktop |
| J3 🔴 | ½ PASS | Number-format half passed (regional format Vietnamese, elevated, readings identical to English). Display-language half deferred by the owner |

Also closed today: **ENG-9**. No temp unpacking was observed, and `build-release.ps1` now fails if
the natives ever end up bundled.

## Observation (minor, not a row)

The report mixes number styles: the **Value** column is always invariant (`18.3 %`), while the
**Details** follow the Windows regional format (`164,9 GB` under Vietnamese). Both are right, and
parsing is unaffected (analyzers enforce invariant parsing), but on a non-English Windows the
inconsistency is visible. Cosmetic; fix only alongside the Vietnamese UI work.

## Still needs a person

B1, B4, E1, G3, D4 (DiskSpd), H5 (DiskSpd), I5 (DiskSpd + USB drive), J1 (a PC without .NET),
plus the person halves of A4, F3 and J2.
