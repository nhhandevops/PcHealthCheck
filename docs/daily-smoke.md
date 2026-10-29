# Daily smoke log

One row per run of `daily-smoke.ps1`, oldest first. The script header says what each column
checks and why. WARN rows are kept, not tidied away: a flaky test or a smoke run on a dirty
tree only means something once it has been counted.

| When | Commit | Privacy gates | Build | Tests | Scan (unelevated) | Backups | Result |
|---|---|---|---|---|---|---|---|
| 2026-10-19 17:43 | `d487c9d+dirty` | PASS / PASS | PASS | 305/305 | 15 done / 2 skipped, Bad | SKIP | **PASS with warnings** |
| 2026-10-21 14:25 | `d487c9d+dirty` | PASS / PASS | PASS | 312/312 | 15 done / 2 skipped, Bad | SKIP | **PASS with warnings** |
| 2026-10-22 11:39 | `584cb5a` | PASS / PASS | PASS | 341/341 | 15 done / 2 skipped, Bad | SKIP | **PASS** |
| 2026-10-23 09:40 | `ddad8bd` | PASS / PASS | PASS | 376/376 | 16 done / 1 skipped, Bad | SKIP | **PASS with warnings** |
| 2026-10-23 10:30 | `ce93142` | PASS / PASS | PASS | 376/376 | 16 done / 1 skipped, Bad | SKIP | **PASS** |
| 2026-10-26 07:17 | `af05a3f` | PASS / PASS | PASS | 502/504, 2 skipped + 45 kit | SKIP | SKIP | **PASS** |
| 2026-10-26 08:49 | `36dfe42` | PASS / PASS | PASS | 502/504, 2 skipped + 45 kit | 16 done / 1 skipped, Bad | SKIP | **PASS** |
| 2026-10-26 11:25 | `d14c59b` | PASS / PASS | PASS | 502/504, 2 skipped + 68 kit | 16 done / 1 skipped, Bad | SKIP | **PASS with warnings** |
| 2026-10-27 09:50 | `c067e3a` | PASS / PASS | PASS | 502/504, 2 skipped + 208 kit | 16 done / 1 skipped, Bad | SKIP | **PASS with warnings** |
| 2026-10-28 13:08 | `2071660` | PASS / PASS | PASS | 781/783, 2 skipped + 285 kit | 17 done / 1 skipped, Bad | SKIP | **PASS with warnings** |
| 2026-10-29 11:41 | `e9b39cd` | PASS / PASS | PASS | 1205/1207, 2 skipped + 286 kit | 17 done / 1 skipped, Bad | SKIP | **PASS** |
