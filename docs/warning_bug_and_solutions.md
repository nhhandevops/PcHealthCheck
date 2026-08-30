# Warnings, bugs and solutions

Faults found while working on this project, and what actually fixed them. Newest first.

Each entry: symptom → root cause → how to verify → fix → date.

Bugs found by *running* the app on real hardware live in `HANDOFF.md` alongside the design
notes they changed. This file is for the ones that would otherwise be forgotten: build,
packaging and tooling faults that no unit test covers.

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
