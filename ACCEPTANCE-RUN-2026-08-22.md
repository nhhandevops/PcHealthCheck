# Acceptance run — 2026-08-22

First pass against `ACCEPTANCE.md`. Recorded because a checklist nobody filled in proves
nothing, and because four rows failed.

| Field | Value |
|---|---|
| Build | 1.0.0-rc.1 (rebuild required — see Outcome) |
| Machine | Windows 11 Pro 26200, i5-12400F, 32 GB |
| Drives | D: ST2000DM008 (SATA HDD) · C:/E: CT1000P3PSSD8 (NVMe) · G: ST1000LM035 (HDD in USB caddy) · F: Realtek RTL9210B (NVMe in USB enclosure) |
| Second account | Yes — `CodexSandboxOffline`, not signed in |
| Display language | English |
| Tester | Automated where possible; rows needing a person are listed at the end |

---

## Outcome

**Do not distribute this exact build.** Four defects were found and fixed; the release must be
rebuilt and the affected rows re-run. No blocking row failed on the *behaviour* the app
promises — the failures were in cleanup, concurrency and packaging.

| | |
|---|---|
| Rows verified | 34 |
| Rows needing a person | 14 (listed below) |
| Rows not testable here | 3 |
| Defects found | 4 — all fixed, all now covered by tests |
| Tests | 269 → 289 |

---

## Passed

**A — Install and start.** Checksum matched the published `SHA256SUMS.txt`. Zip extracts to
11 files. Product, version, Company and Copyright all populated; icon embedded. Report prints
`1.0.0-rc.1`, pre-release suffix intact. No UAC prompt at any point.

**C — The promises about your data.** The important ones, measured rather than asserted:

- **C1** — 165 samples of the process's TCP and UDP endpoints taken across a full 2½-minute
  scan: **zero connections**.
- **C2** — Receipt lists 9 external processes, every one with its full command line, and 4
  file writes, every one marked deleted.
- **C3** — Every drive root and the Documents folder photographed before and after a full
  scan: **no difference at all**.
- **C6** — Report read end to end. The only hit for "password" is advice text about password
  managers. No keys, no file contents.
- **C7** — No `http`, no `https`, no `<script>` anywhere in the exported HTML.

**D — Disk benchmark.** Test folder observed appearing and disappearing on all four physical
drives. Receipt records the size actually written (1 GiB internal, 256 MiB external). Speeds
plausible: HDD 174 MB/s, NVMe 3,631 MB/s, USB drives reported without a grade.

**E — Honesty when something cannot be checked.** Verified accidentally, which is the best
kind: the benchmark genuinely failed on G: mid-run, and the app reported
`PartiallyCompleted`, named the drive, quoted the error and raised the amber box. On a clean
run the box stays away. Every `Skipped` module gives a reason.

**F — Trends.** 9 scans over 2.6 days, **zero false regressions** — despite the benchmark
parameters having changed since the older readings were taken (D: moved 210 → 174 MB/s, a 17%
drop, correctly inside the noise floor). `--no-history` reads and writes nothing.

**H5** — The ledger holds only `pawnio`, which the app installed. DiskSpd, installed outside
the app, is absent, so the app will never offer to remove it.

---

## Failed, and fixed

### 1. A killed benchmark left a gigabyte behind forever — row I2

Killing the process mid-benchmark left `D:\PcHealthCheck-bench\bench.tmp` (1 GiB) with
nothing to ever remove it. The module deletes in a `finally`, which covers cancelling and
failing but not being killed, crashing, or losing power. The original plan called for an
orphan sweep; it was never written.

**Fixed** — the sweep runs when the app opens and before each benchmark, and reports what it
reclaimed rather than deleting in silence. Verified live: the leftover gigabyte disappeared on
next launch. Tests cover what it can reach, including an unrelated file placed in that folder
surviving untouched.

### 2. Two copies both measured half speed and called it a result — row I3

Two instances at once both reported `Completed` and both recorded roughly **half** each
drive's real speed, because they were competing for the drives. Neither said why. One also
failed to delete its own test file — the other had removed the folder underneath it. With
trend history on, those halved numbers become the baseline everything later is judged against.

**Fixed** — a lock file gates the benchmark; the second instance measures nothing and says so.
Each instance writes `bench-<pid>.tmp`. Re-run: one measured at full speed (D: 184.7,
E: 4,858), the other skipped with a reason, nothing left behind.

*A test caught a second bug inside the first fix.* The obvious gate is a named mutex — but a
mutex belongs to the thread that took it, and the benchmark is async, so the release can land
on a different thread pool thread, throw, and leave the lock held. Every scan after the first
in that session would have silently skipped the benchmark.

### 3. A device failure was reported as raw tool output — row D6 area

The benchmark failed on G: with `Error while writng during file creation (error code: 1117)` —
DiskSpd's own typo included. Windows 1117 is a device I/O error, and **a drive that cannot
complete a write is a far more serious finding than the speed it was being measured for**.
The user was left with a number nobody recognises.

**Fixed** — known error codes are translated into plain language leading with "BACK THIS DRIVE
UP". Unrecognised failures are still passed through untouched rather than dressed up.

### 4. The checksum file had a byte-order mark — row A1

`SHA256SUMS.txt` was written UTF-8 with BOM, which makes the first line unreadable to
`sha256sum -c` and most checksum tools — breaking the only integrity check an unsigned
download has.

**Fixed** — written as ASCII.

---

## A real finding about this machine, not the app

**G: (ST1000LM035 in a USB caddy) is failing under sustained write.** Writing a gigabyte to it
produced 25–32 Windows I/O retries; writing 256 MB produced an outright `ERROR_IO_DEVICE`.
Retries escalating to hard errors is the usual progression. Back that drive up, then try a
different cable and a port directly on the motherboard before assuming the drive itself.

**F: is connected at USB 2.0 speed** — 38 MB/s from an NVMe SSD in a 10 Gbps enclosure, about
5% of what it can do.

---

## Rows a person still has to run

Nothing here can be automated; each needs a human at the keyboard or a second machine.

| Row | Why it needs you |
|---|---|
| A3 | SmartScreen only appears on a *downloaded* file. A locally built exe has no Mark-of-the-Web, so this proves nothing here — test from a real download. |
| A7 | Window resizing — needs eyes. |
| B1, B2, B3, B5, B6, B7 | Consent screen, UAC prompt appearing exactly once, declining it, choices remembered. |
| C4, C5 | The Delete history button. |
| E1 | Declining a UAC prompt and confirming the amber box appears. |
| G1–G7 | Defender scan consent wording; VirusTotal needs your own API key. |
| H1, H2, H3, H4, H6 | Installing an optional tool (one UAC prompt) and the removal offer at exit. |
| I1 | Cancel button mid-scan — the automated test used a hard kill, which is stricter but different. |
| I4, I5 | Unplugging a drive, before and during a benchmark. |
| J1, J2, J3, J4 | Another machine: no .NET SDK, a laptop, **a Vietnamese Windows**, and a non-administrator account. |

**J3 is the one worth going out of your way for.** Locale parsing is covered by unit tests, but
a decimal-comma Windows is where a speed or a temperature silently comes out 1000× wrong, and
this project has already shipped two locale bugs.

---

## Before the next run

1. Rebuild: `.\build-release.ps1` — the current zip predates all four fixes.
2. Re-run rows **A1, D3, D4, I2, I3** against the new build.
3. Work the person-required table above.
