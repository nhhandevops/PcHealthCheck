# Warnings, bugs and solutions

Faults found while working on this project, and what actually fixed them. Newest first.

Each entry: symptom → root cause → how to verify → fix → date.

Bugs found by *running* the app on real hardware live in `HANDOFF.md` alongside the design
notes they changed. This file is for the ones that would otherwise be forgotten: build,
packaging and tooling faults that no unit test covers.

---

## CI-001 — CI red on a good commit: artifact storage quota full

**Symptom.** The push of `cb6a819` failed CI, but the privacy gate, build, tests, publish and
checksums all passed. Only `actions/upload-artifact` failed:
`Failed to CreateArtifact: Artifact storage quota has been hit.`

**Root cause.** Every push uploads the self-contained app (~47 MB) and keeps it for GitHub's
default 90 days. After 23 pushes, 1,070 MB were stored, the oldest not expiring until
2026-11-20.

**How to verify.** `gh api repos/nhhandevops/PcHealthCheck/actions/artifacts --jq '.total_count'`, plus the
sum of `size_in_bytes`. Check which step failed before assuming the code is broken:
`gh run view <id> --json jobs --jq '.jobs[].steps[] | "\(.name): \(.conclusion)"'`.

**Fix.** All 23 old artifacts were deleted (each is rebuildable from its commit), and `build.yml` now
sets `retention-days: 7`. GitHub recalculates usage every 6–12 hours, so the first run after the
cleanup can still fail at the upload step. Two re-runs two hours after the cleanup still hit the
quota, so the upload step was also given `continue-on-error: true`. The privacy gate, build and
tests still fail the run as before. A failed upload now shows as an annotation on a green run, so
look at the run's warnings when a downloadable build is actually needed.

**Date.** 2026-10-21.

---

## HW-001 — First full audit of a real laptop: four things the app misgraded

**Symptom.** On 2026-10-21 an elevated `--headless-scan --include-admin` of a ThinkPad T470p
(i7-7820HQ, 16 GB, Intel SSD Pro 1500, battery at 50%) was compared with an independent
read-only audit of the same machine. Most grades matched. These four did not:

| Metric | App said | Reality on this machine |
|---|---|---|
| `os.supported` | **Bad**: "no more security updates, even with ESU" | Consumer ESU was extended to **2027-10-12** on 2026-06-25 ([Microsoft](https://www.microsoft.com/en-us/windows/extended-security-updates)). `SysOverviewModule.cs:11-13` still hard-codes 2026-10-13. |
| `memory.used_pct` | **Good** (83%) | Commit hit 99.2–99.9% of the 32.49 GB limit three times in three days (System event 2004, Resource-Exhaustion-Detector), and dwm.exe crashed with 0xc00001ad (fatal memory exhaustion). The app reads only physical-memory use and has no commit or event-2004 check. |
| `storage.wear_pct` for a USB NVMe in a Realtek RTL9210B-CG enclosure | **Good** (0%) | The bridge passes no SMART. `Get-StorageReliabilityCounter` returns Wear=0, Temperature=0 and every other field empty, which is a placeholder, not a measurement. **Rule 7 violation:** an unread value graded healthy. |
| `sensors` | Completed, only `gpu.temperature_c` | There is no CPU temperature without PawnIO, by design. But the ACPI zone (`\Thermal Zone Information(*)\Temperature`, readable unelevated) showed 90–97 °C, and the module never consults it. |

Also: `EventsModule.cs:35` counts disk errors from `disk/stordisk/storahci/stornvme/Ntfs` but not
`iaStorAC`/`iaStorAVC` (Intel RST) or `volmgr`, and it does not say which disk an event names. Here
both counted "disk errors" came from a USB backup drive, not the internal SSD.

Confirmed in the follow-up gap review (full matrix in `docs/feature-gap-2026-10-21.md`):

- With `--no-history`, the HTML still says "This is the first recorded scan… run the app again"
  (`HtmlReportExporter.cs:116-123`). Nothing was recorded, so the sentence is false.
- The Sensors card reads **Good** when the only sensor read was the GPU. The CPU temperature
  was never read, which breaks rule 7 at the category level.
- On a first scan with no history, a lifetime CRC (199) count of 25 is graded **Bad**, with "replace
  the SATA data cable". Nothing shows the count is rising, and CounterGrowth already handles
  escalation.
- The SSD temperature row's Detail reads "Mechanical drives last longest below about 40 °C"
  (`DiskSmartModule.cs:145`).

**Not a bug, checked:** `smart.predict_failure = true` graded Good is correct. The value is
`!predict` (`DiskSmartModule.cs:79`), so true means "no failure predicted". The id reads backwards.

**How to verify.** Raw snapshot and audit report were in the 2026-10-21 session scratchpad (not in
git). To reproduce: `Get-PhysicalDisk | Get-StorageReliabilityCounter` (admin) on any USB-bridged
SSD; `Get-WinEvent -FilterHashtable @{LogName='System';Id=2004}` on a machine under memory
pressure.

**Fix — proposed, NOT done.** (1) Move the ESU end date to 2027-10-12 and keep it in data, not
in an inline constant, since Microsoft has now moved it once. (2) Add a commit-charge metric
(`\Memory\% Committed Bytes In Use`) plus an event-2004 count. (3) Treat a reliability counter
whose Wear and Temperature are both 0 and whose other fields are all empty as **not read**. (4) Let
`sensors` fall back to the ACPI thermal zone, labelled as a zone reading rather than "CPU
temperature".

**Date.** 2026-10-21.

---

## PRIV-001 — The privacy gate could never fail, and a correct text scan still misses the main leak

**Symptom.** None, which is the problem. CI's "Verify no network code outside
PcHealth.Net.Optional" step and the same check in `build-release.ps1` printed `OK` on every run.
They would have printed `OK` on a file that said `using System.Net.Http;`.

**Root cause — two holes, one under the other.**

1. `Select-String -Pattern 'System\.Net' -SimpleMatch`. `-SimpleMatch` means *literal*, so the
   regex escape is taken at its word: the gate searched for the text `System\.Net`, backslash
   included, which no C# file contains. It has passed everything since it was written.
2. Fixing the pattern is not enough. `ImplicitUsings` is on in `Directory.Build.props`, and the SDK
   generates `global using System.Net.Http;` into every project's `obj\…GlobalUsings.g.cs` — a
   folder the gate deliberately skips. So `new HttpClient()` compiles in *any* project with no
   `using` line, and the text "System.Net" appears in no file the gate reads.

The promise itself held: with both holes closed, today's tree has zero `System.Net` use outside
the opt-in assembly. Nothing leaked. The gate simply could not have said so if something had.

**How to verify.** Prove a gate can fail before trusting that it passes — feed it a leak:

| Gate | File with `using System.Net.Http;` | `new HttpClient()`, no using line |
|---|---|---|
| As shipped (`'System\.Net'` + `-SimpleMatch`) | 0 hits — **missed** | 0 hits — **missed** |
| Pattern fixed (`'System.Net'`) | 1 hit | 0 hits — **missed** |
| Compiled-assembly scan | 1 hit | 1 hit |

Measured 2026-10-19 with a throwaway project in the scratchpad. On the real Release build the
compiled scan reports 0 for `PcHealth.Core`, `PcHealth.Ipc`, `PcHealth.Modules.Windows` and
`PcHealthCheck.dll`, and 6 for `PcHealth.Net.Optional.dll`.

**Fix.** The pattern is corrected in `build.yml` and `build-release.ps1` (hole 1). Hole 2 is
closed by `daily-smoke.ps1`, which also scans the compiled assemblies: every type an assembly uses
is named in its metadata as UTF-8, so a `System.Net` type leaves that string in the DLL however
it got into scope, while string literals are UTF-16 and cannot cause a false hit. The scan checks
itself on each run — `PcHealth.Net.Optional.dll` must be seen to contain `System.Net`, or the
step fails as untrustworthy.

**Not yet done.** CI and `build-release.ps1` still have only the text gate, so hole 2 is caught
by the daily smoke but not at push or release time. Porting the compiled scan into both is the
remaining step.

**Rule this leaves behind.** A gate that has never failed has not been tested. Before relying
on any check that guards a promise, run it once against something it must reject.

**Date.** 2026-10-19.

---

## PKG-002 — `sha256sum -c SHA256SUMS.txt` fails on every line: CRLF

**Symptom.** The published checksum file looks correct — the hashes match when compared by
eye, and there is no byte-order mark — but the tool a downloader would actually use rejects
every entry:

```text
sha256sum: 'PcHealthCheck-1.0.0-rc.1-win-x64.zip'$'\r': No such file or directory
PcHealthCheck-1.0.0-rc.1-win-x64.zip: FAILED open or read
sha256sum: WARNING: 1 listed file could not be read
```

Exit code 1. To anyone verifying a download, that is indistinguishable from tampering.

**Root cause.** `Set-Content -Encoding ASCII` on Windows PowerShell 5.1 writes **CRLF**. GNU
`sha256sum -c` treats everything after the two-space separator as the filename, carriage
return included, so it looks for a file whose name ends in `\r` and never finds it.

This is the *second* form of the same defect. PKG-001 (below) fixed a byte-order mark that
broke the first line; the encoding change removed the BOM but left the line endings, so the
file went from "first line unreadable" to "every line unreadable". Both times the hashes
themselves were right, which is why comparing them by hand reported success.

**How to verify.** Do not read the file — run the tool on it:

```bash
cd release
od -c SHA256SUMS.txt | tail -3          # look for \r \n at the end of a line
sha256sum -c SHA256SUMS.txt; echo $?    # must print OK and exit 0
```

**Fix.** `build-release.ps1` now writes the bytes itself instead of asking a cmdlet to —
UTF-8 with no BOM, joined with `` "`n" `` — and then **verifies what it wrote**: it rejects a
leading BOM, rejects any `0x0D` byte, and runs `sha256sum -c` for real, throwing if the exit
code is non-zero. Git for Windows ships `sha256sum` under `usr\bin`, which is not on
PowerShell's `PATH`, so the script looks there explicitly rather than downgrading to the byte
heuristics on every machine that has the tool installed.

The same run removed the `PcHealthCheck.exe` line from the file. The exe lives inside the
archive, so that line could never be checked from the directory the checksum file sits in:
`sha256sum -c` reported `FAILED` and exited 1 on a release where nothing was wrong. Its hash
is still printed at the end of the build for the release notes and the winget manifest. **An
integrity check that cries tampering at a healthy download is worse than one that covers
less.**

**Rule this leaves behind.** A file written on Windows for a POSIX tool to read is not
verified by looking at it. Run the tool. Both times this shipped, the content was correct and
the encoding was not — the only check that would have caught either is the one the reader
actually performs. See also OPS-004 in the global rules, which describes this exact
PowerShell trap.

**Date.** 2026-08-26.

---

## PKG-001 — `SHA256SUMS.txt` written with a byte-order mark

**Symptom.** The first line of the checksum file is unreadable to `sha256sum -c` and to most
checksum tools, breaking the only integrity check an unsigned download has.

**Root cause.** `Out-File` / `>` / `Set-Content` default to UTF-8 **with BOM** on Windows
PowerShell 5.1.

**How to verify.** `head -c 3 SHA256SUMS.txt | od -An -tx1` — `ef bb bf` means a BOM.

**Fix.** Written as ASCII. *Superseded by PKG-002*, which found the fix incomplete: it removed
the BOM but left CRLF, so the file was still unreadable.

**Date.** 2026-08-22. Found during the first acceptance run; see
`ACCEPTANCE-RUN-2026-08-22.md`.

---

## WARN-00x — `Worker_rejects_module_ids_that_are_not_in_its_own_registry` trượt khi chạy cả bộ, pass khi chạy riêng

**Triệu chứng.** `dotnet test` toàn bộ: `Failed: 1, Passed: 304, Total: 305`, trượt đúng
`WorkerProtocolTests.Worker_rejects_module_ids_that_are_not_in_its_own_registry`.
Chạy riêng đúng test đó: `Passed: 1, Failed: 0`.

**Nguyên nhân gốc (giả thuyết, CHƯA chứng minh).** Các test trong `WorkerProtocolTests` khởi chạy
`PcHealthCheck.exe` như tiến trình con thật. xUnit chạy các test collection song song, nên nhiều
tiến trình worker cùng tồn tại và tranh nhau tài nguyên/thời gian chờ. Đây là vấn đề **cô lập
test**, không phải lỗi sản phẩm.

**Bằng chứng đã có.**
- `tests/PcHealth.Tests/WorkerProtocolTests.cs` **không nằm trong đợt sửa** này (`git diff --cached`
  không liệt kê) — test cũ, không ai đụng vào.
- Chạy đơn lẻ pass ngay lần đầu.
- Build sạch: 0 warning, 0 error.

**CHƯA làm — việc còn lại.** Chưa chứng minh nó pass ở commit trước đợt sửa này. Cách kiểm không
phá working tree: `git worktree add /tmp/pchc-prev <commit-truoc>` rồi build + test trong đó.
Nếu cũng trượt thì đây là flaky có sẵn; nếu pass thì phải soi `ElevatedWorkerSession.cs`.

**Cách xử lý đề xuất.** Đánh dấu `WorkerProtocolTests` vào một xUnit collection riêng để tắt chạy
song song cho nhóm này, thay vì tăng timeout.

**Ngày:** 2026-08-30
