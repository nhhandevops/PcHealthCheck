# Daily smoke log

One row per run of `daily-smoke.ps1`, oldest first. The script header says what each column
checks and why. WARN rows are kept, not tidied away: a flaky test or a smoke run on a dirty
tree only means something once it has been counted.

| When | Commit | Privacy gates | Build | Tests | Scan (unelevated) | Backups | Result |
|---|---|---|---|---|---|---|---|
| 2026-10-19 17:43 | `d487c9d+dirty` | PASS / PASS | PASS | 305/305 | 15 done / 2 skipped, Bad | SKIP | **PASS with warnings** |
| 2026-10-21 14:25 | `d487c9d+dirty` | PASS / PASS | PASS | 312/312 | 15 done / 2 skipped, Bad | SKIP | **PASS with warnings** |
