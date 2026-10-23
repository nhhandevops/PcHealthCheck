# Feature gap: what the app reports vs a full hardware audit (2026-10-21)

On 2026-10-21 a ThinkPad T470p (i7-7820HQ, 16 GB, Intel SSD Pro 1500, battery at 50%) got an
independent, read-only, multi-agent hardware audit covering battery, RAM, CPU and disk. Each
component got health, usage, a change-now/later/good verdict, reasons, when-to-change triggers
and search keywords. The same morning, the app ran
`--headless-scan --include-admin --no-history` on the same machine. This file records which of
those six things the app can produce, and how its results compared. The verified misgrades are in
`docs/warning_bug_and_solutions.md` (HW-001).

**How far to trust this.** Five agents built the matrices. Independent reviewers checked the
battery matrix (code and results) and the RAM matrix (results). The CPU, disk and presentation
reviewers hit a usage limit and did not run. Every P1 below was then confirmed by hand against
code and output. The P2 and P3 items in those three areas are unreviewed.

## Feature coverage

| | Health | Usage | Change now / later / good | Reason | When to change | Keywords / deep-dive |
|---|---|---|---|---|---|---|
| **Battery** | Yes: capacity %; cycles shown as info only | No | Partial: 3 grades, one tier too urgent here | Yes: detail + tips | Partial: fixed text; dated projection needs 14+ days and only while above 60% | No (the receipt shows the command run) |
| **RAM** | Partial: WHEA and bugcheck counts only | Partial: physical % only, admin-only | No RAM verdict (filed under Sensors) | Partial, partly misleading | No | No |
| **CPU** | Partial: WHEA and Kernel-Power 41; temperature needs PawnIO | No: no load, clock or top processes; benchmark needs 7-Zip | No CPU verdict | Partial | Partial: benchmark trend needs 7-Zip + history | No |
| **Disk** | Yes when elevated: wear, reallocated, uncorrectable, predict-failure, CRC, temperature, TRIM, volumes | Partial: free space, TRIM | Partial: Storage grade, no per-drive decision | Yes: detail + tips | Partial: wear projection and counter growth need history | No (details name SMART attribute numbers) |

**Not anywhere in the app:**
- search keywords
- per-component verdicts for RAM and CPU
- top consumers (processes)
- usage figures for battery and CPU
- the threshold behind a grade

Verdict.Forecast is a fixed sentence on 7 of 95 rows. The only dated "when" is a TrendAnalyzer
projection, which needs 14+ days of history.

## Results on this laptop

| Component | App said | Audit said | Match |
|---|---|---|---|
| Battery | Bad, "Action needed" (50%) | Plan soon: no fault signs, always on AC | Value matches; urgency one tier high |
| RAM | Good (83% physical, admin only) | **Act now**: commit at 99.2–99.9% three times, dwm crashed with 0xc00001ad | **Wrong** |
| CPU | No CPU grade; Sensors "Good" from GPU 74 °C only | Plan soon: 90–97 °C, about 2.0–2.4 GHz under full load | **Missing**; Sensors Good breaks rule 7 |
| Disk (internal) | Storage Bad (CRC 25 + no backup) | Monitor; backup **Act now** | Backup right; CRC over-alarmed on a first scan |
| Disk (K:, USB) | 0% wear, Good | Not readable (bridge passes no SMART) | **Wrong** (rule 7) |
| Platform | "Out of support, no updates even with ESU" | ESU runs to 2027-10-12 | **Wrong** |

## Order of work

P1: the app shows something wrong, or shows an unread value as healthy. All confirmed by hand.
**All five done on 2026-10-21. See HW-001 for what changed and how it was verified.**
1. Windows 10 end date: 2027-10-12, kept in data rather than a constant (`SysOverviewModule.cs:11-13`).
2. Memory: add commit charge vs limit and an Event 2004 count, and give RAM its own card instead of "Sensors" (`SensorsModule.cs:242-247` reads physical load only).
3. Rule 7, two places:
   - The Sensors card must not read Good when the CPU temperature was not read.
   - In `StorageReliabilityModule`, a reliability counter with Wear 0 and Temperature 0 and every other field empty counts as **not read**.
4. Disk-error events: name the drive each one came from, and add `iaStorAC`/`iaStorAVC`/`volmgr` (`EventsModule.cs:35`).
5. With history disabled, stop printing "This is the first recorded scan… run again" (`HtmlReportExporter.cs:116-123`).

P2: important missing signals. Unreviewed outside battery and RAM.

6. CPU: fall back to the ACPI thermal zone (readable unelevated), add effective clock under load (`% Processor Performance`), and add Kernel-Power 86 and thermal sleeps.
   **Done:** the thermal-zone fallback on 2026-10-21, and clock under load on 2026-10-22 (`bench.cpu.clock_pct_of_base`, 87% on the T470p). Thermal sleeps are deliberately not built: the "Reason" code for a heat-triggered sleep cannot be verified on a healthy machine (codes confirmed here: 4, 6, 7). See HANDOFF.
7. Lifetime CRC (199) on a scan with no history: grade Normal plus "watch for growth" instead of Bad. CounterGrowth already escalates it. **Done 2026-10-22.**
8. Battery:
   - Show the pack identity: `Win32_Battery.Name` (already fetched) and the XML Id, Manufacturer, Chemistry.
   - Show the pack age (SMBIOS type 22).
   - Show charge state and AC/DC time.
   - Make urgency depend on usage: a desk-bound laptop is not "Action needed".
   **Done 2026-10-22:** identity, time on battery, and usage-aware wording. Pack age and a dated fade projection were added the same day. Charge state is still open.
9. SMART: spare blocks (170/232), program/erase fails (171/172), SATA downshift (183), host writes vs rated TBW.
   **Done 2026-10-22, vendor-neutral:** each attribute is compared with its maker's own failure limit (`smart.attributes_at_threshold`). That covers spare blocks without hard-coding Intel. 171/172/183/241 are left uninterpreted on purpose, because their meaning differs by brand.
10. Top memory and CPU consumers in the report. **Done 2026-10-22** (names only; privacy documents updated).
11. A battery projection once below 60% (for example, toward 40%).

P3: nice to have.

12. A "look deeper" line per finding: event IDs, SMART attribute names, the read-only command.
13. Fix the SSD temperature text (`DiskSmartModule.cs:145` says "Mechanical drives…").
