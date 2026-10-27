# Warnings, bugs and solutions

Faults found while working on this project, and what actually fixed them. Newest first.

Each entry: symptom → root cause → how to verify → fix → date.

Bugs found by *running* the app on real hardware live in `HANDOFF.md` alongside the design
notes they changed. This file is for the ones that would otherwise be forgotten: build,
packaging and tooling faults that no unit test covers.

---

## KIT-007 — Closing the kit's window at the questions lost a complete scan

**Symptom.** On 2026-10-26 the owner ran the kit on the T470p, with administrator rights. The scan
finished in 68 s with all 22 checks Completed. The window was then closed at the first question.
The stick held no zip, only `Results\.work-<name>`, and the fleet summary, which reads zips only,
could not see it. On a company laptop that would have been a silently missing laptop.

**Root cause.** The kit wrote `kit.json` and the result zip only in its `finally` block. Closing a
console window kills PowerShell outright, and no `finally` runs. Everything else was already on the
stick by then; only the step that makes it a result was lost.

**The wrong first guesses, two of them.**

- **That `finally` was enough.** It covers errors and Ctrl+C, and the kit had been tested with
  both, but closing the window was never tried. It is also the most natural thing for a person to
  do once the window says "Finished in 68 s; every check ran."
- **That saving the zip early was the fix.** My first version did only that, plus reading unzipped
  work folders in the summary. A review round of three agents killed real runs at every moment and
  proved three high-severity holes in it:
  - **The final save deleted the early zip and compressed in place.** Compress-Archive writes the
    whole file at its end, so a window closed in those seconds (right when the old kit showed the
    verdict, which the guide told the operator to read and then close) left a zip nothing could
    open. The summary showed an anonymous UNREADABLE row.
  - **The summary trusted the early zip over a work folder that held the answers.** A window
    closed after the answers were saved, but before the final zip, turned a laptop answered
    "the battery bulges" into READY.
  - **The app outlives the window.** It is a windowed program, so closing the kit's window does not
    stop it. It finished its scan and left `%LOCALAPPDATA%\PcHealthCheck\temp` on the laptop, and no
    cleanup of the kit's ever ran. This was older than KIT-007.

  The same round found smaller faults, all fixed here: answers held only in memory; a rerun in the
  same minute reusing the old work folder, which the surviving app still held open; "(exit code )"
  and battery or Windows noise on runs that never scanned; anonymous UNREADABLE rows; serials parsed
  wrongly from names that contain "_"; and abandoned no-serial attempts counted as extra laptops.
- **That the table should show each laptop's latest run.** A second review round, of four agents,
  found that the redesign still lost findings, all fixed here:
  - **A later unfinished run erased an earlier finished one.** A laptop checked to the end with
    "the battery bulges", then rechecked and closed before any answer, showed only the second run:
    CHECK AGAIN, with the swollen battery gone. The table now shows a laptop's latest *finished*
    run and names the later unfinished one in its notes.
  - **My fix for no-serial attempts dropped real laptops.** It dropped any unfinished no-serial run
    whose computer name matched a finished one, and laptops cloned from one image share a computer
    name. A laptop with a swollen battery vanished from the table. Runs are now grouped by serial,
    or else by the hardware UUID, which the kit now records; never by computer name.
  - **Same-minute runs sorted wrongly.** A folder's name starts with `.work-`, so it sorted before
    every zip, and a second run's answered folder lost to the first run's early zip. Runs are now
    ordered by their start time, then by name with the prefix removed.
  - **A "no" was saved only after its note.** Closing the window at "What is wrong?" lost the
    answer that the battery bulges. It is now saved, and the fire warning shown, before the note.
  - **An interrupted run could be READY without anyone looking at the battery.** A run closed
    before the battery question now asks for it to be answered, on a laptop.
  - **Smaller faults:** a typed serial and its file-name form were two laptops; an UNREADABLE
    zip of serial "AB_12" was parsed as laptop "AB"; a `kit.json` caught mid-rewrite ranked a
    folder full of answers below an early zip; a report linked with no JSON beside it; and
    `--temp-dir` accepted a missing or relative path.
- **That one run should decide a laptop's row.** A third round, of three reviewers and nine
  skeptics, proved that showing the latest finished run hid what other runs recorded, in both
  directions, and that the kit's own last step was wrong. All fixed here:
  - **A later unfinished check was reduced to a date.** Checked to the end and READY, then checked
    again with "the battery bulges" and closed at the note: READY, the swollen battery nowhere. The
    same with a failing drive found by the recheck. With no run finished, the latest run hid an
    earlier one's swollen battery the same way.
  - **A finished recheck that skipped the battery question erased "the battery bulges".**
  - **Ctrl+C recorded "complete".** Ctrl+C, unlike a closed window, runs the `finally` block, and
    it wrote `complete` whatever had happened. Ctrl+C at the battery question gave READY on a
    question nobody answered, and after one answer the "N of 11 not reached" note was gone.
  - **The first answer could be lost between two writes.** The kit saves the answer, then rewrites
    `kit.json`. A window closed between the two left "scanned" beside the answer, the same rank as
    the early zip, and the zip won the tie.
  - **The `.tmp` move is not atomic.** `Move-Item -Force` on Windows PowerShell 5.1 deletes the old
    file and then renames the new one, and a probe found the file missing in 3.5% of polls during
    rewrites. A window closed in that gap left the answers only in `manual-checks.json.tmp`, which
    nothing read: READY.
  - **Two laptops given the same typed text were one laptop**, because a serial was trusted over the
    hardware UUID. One laptop and its swollen battery vanished. And one laptop was two rows when one
    run had a typed serial and another none.
  - **This laptop's real hardware UUID was in a test fixture**, about to be published. It was
    replaced with a made-up one, and a kit check now fails on any UUID in the kit or its tests that
    is not a placeholder or obviously made up.
  - **Smaller faults:** a stick that turned read-only during the questions dropped every answer
    without a word; a no-serial run closed before `machine.json` was written could not be matched to
    its laptop; `--temp-dir` accepted "C:temp"; and several documents still told the operator to copy
    only the zips.
- **That the hardware UUID and the clock could be trusted.** A fourth round, of three reviewers and
  six skeptics, re-ran every third-round reproduction (all fixed) and then attacked the new rules:
  - **Laptops that share a UUID became one row.** Firmware reuses UUIDs: Dell's for a blank service
    tag is the same on every such laptop, and boards whose identity was never programmed share one.
    Grouping by UUID first merged them, and one laptop, with its swollen battery, vanished. A
    placeholder UUID made it worse: such a laptop typed "unknown" joined another laptop typed the same.
  - **A wrong clock decided which check was later.** An old laptop's clock comes up reset, to 2018
    say, and nothing corrects it during the run. A finished check with "the battery bulges" dated 2018
    sorted before an unfinished check's "ok", which then cleared it: READY.
  - **An empty work folder counted as a finished check.** A window closed in a run's first second
    leaves a folder with nothing in it, and the summary's stand-in `kit.json` for a missing one ranked
    it complete, so it replaced an earlier finished check.
  - **A repair could not be shown, or was undone.** A re-check closed at the optional last remark
    never counted as finished, so its Good drive never replaced the old fault; and a later check
    without admin rights brought back a drive fault that an earlier finished check had read as fixed.
  - **Ctrl+C in the last second of a scan could delete it.** The app, still running, wrote its report
    after Compress-Archive had looked in the report folder; the zip went without it, and the kit then
    deleted the folder that held it.
  - **Smaller faults:** notes containing "; ", which the table splits into two; a battery fault listed
    once per check, its percentage different each time; checks of the same minute mislabelled; a
    warning that the answers were lost after a save that had recovered; and TOOL-004's trigger, wider
    than I wrote.
- **That patching the trust rules would converge.** A fifth round, of two reviewers and four
  skeptics, found each fourth-round rule broken in a new way:
  - **The clock test depended on the day the summary ran.** A check dated two days ahead was caught
    only until that date passed; a summary run on a PC whose clock was behind flagged every check.
    And one wrong clock froze a laptop for good: no repair checked again to the end could clear it.
  - **The firmware serial was trusted too much.** A laptop with no serial was merged into a laptop
    with a firmware serial whenever their UUIDs matched, and two laptops reporting a placeholder
    serial the kit did not know ("Not Available") became one.
  - **De-duplicating carried reasons by their first words dropped a second failing drive.**
  - The fix is a different design, not another patch: checks are ordered by the stick, not the
    clock (each run lists fingerprints of the results already there), and runs are grouped so that
    any doubt keeps them apart.
- **A confirmation round** re-ran every fifth-round case with real kit runs (a stub app, made-up
  hardware): all fixed. It found the design's edges, all fixed here: two checks made on two sticks,
  or with a result taken off the stick in between, were still put in order by their clocks, so a
  check dated ahead still cleared a later swollen battery (their order is now unknown); a laptop with
  no serial on a UUID that a firmware-serial laptop shares was one row per check; a typed-serial
  laptop and a no-serial laptop on one UUID were merged (no-serial runs now join only under the same
  computer name); and the note on two rows with one serial named the later row by its clock.
  Two limits remain, both written down: a repair checked on a different stick keeps the old finding,
  and two cloned laptops with no serial, one computer name and one shared UUID are one row.

**How to verify.** Close the kit's window at each moment and run the fleet summary. Feed answers
through a `cmd` pipe that stays open (TOOL-003 says why not .NET):

- **during the scan:** nothing appears under `%LOCALAPPDATA%\PcHealthCheck`, the app finishes the
  scan into the stick folder, and the row says so;
- **after answering "the battery bulges":** the row carries SWOLLEN BATTERY;
- **a normal run:** only the zip is left, and the verdict appears after "Saving".

**Fix.**

- **`kit.json` is written at every stage:** `started`, `scanned`, `answering` (after every answer,
  with `manual-checks.json`), and `complete`. A "no" is saved before its note is asked. Each file
  is written to a `.tmp` name and moved into place; the summary reads the `.tmp` when the file
  itself is missing (`Read-KitFile`). The final `kit.json` also holds the verdict and its reasons,
  which the transcript, closed before they are shown, cannot.
- **Only a run that reached its end is `complete`.** Ctrl+C and errors run the last step, which
  now records the stage the run had reached and `StoppedEarly` (`interrupted` or `error`), and the
  notes say which. `kit.json` also records `SkipQuestions` and the hardware UUID from the start.
- **A stick that stops taking answers is said out loud**, in red, at the first failed save and
  again at the end, with the problems found; the on-screen verdict then uses the answers as given.
- **The zip is saved right after the scan, before the questions.** The transcript is held open for
  writing, so the early save pauses it around the zip and resumes it with `-Append`.
- **Every zip is written as `.partial-<name>.zip`** and renamed over the real one only once it is
  complete. The only gap is between deleting the old zip and the rename, and the work folder still
  holds everything then.
- **The verdict is shown only after the zip is saved.**
- **The app's temporary files go to the stick.** The app gained `--temp-dir`, and the kit points it
  at its work folder, so the app writes none of its files to the laptop's disk even if the window is
  closed mid-scan. The cleanup after the scan stays as a safety net. A missing path, or one that is
  not fully qualified ("C:temp" is rooted, and still relative), is refused before anything is
  scanned, and the kit resolves its results folder to a full path. The daily smoke passes
  `--temp-dir` too, and fails if the receipt lists any file written outside it.
- **A result name gets seconds appended if its zip or its work folder already exists.**
- **The summary uses whichever of a run's zip and work folder got further.**
  - The order is `Get-StageRank`: a zip with no Stage counts as complete, and so does a folder from
    an older kit that has a `kit.json`.
  - An unreadable zip loses to its folder.
  - Whether the scan finished is decided by whether a report exists, because the app can finish
    after the kit recorded `started`.
  - UNREADABLE rows say which laptop and file they are.
  - Names are parsed from the right, by `Get-ResultNameParts`.
  - A run ranks by the furthest of its `kit.json` stage and what is beside it: saved answers, then
    a report. So does a `kit.json` that cannot be read.
- **The summary groups runs into laptops by `Group-AuditRuns`, keeping them apart when in doubt**
  (a wrong merge hides a laptop, a wrong split only shows one twice): one usable UUID is one laptop
  unless its runs carry different serials; a run with no serial joins a UUID laptop only when that
  laptop's serial was typed; a run with a serial and no usable UUID joins the one UUID laptop
  carrying its serial only if its kit recorded no UUID at all (every kit before KIT-007) or it is an
  unreadable zip. Never the computer name. The same serial on two boards is two rows, each naming
  the other. Placeholder serials and UUIDs (Dell's blank service tag among them) name nothing; the
  serial list is shared by the kit and the summary (`Test-PlaceholderSerial`).
- **Checks are ordered by the stick, not the clock.** Each run records in `kit.json` EarlierRuns,
  fingerprints (`Get-RunHash`) of the results already on the stick when it started; a run that lists
  another came after it. Only checks that never shared a stick are ordered by their clocks, and not
  when a clock is plainly wrong (dated before its kit, or contradicted by EarlierRuns). The summary
  never reads its own PC's clock. The kit warns when an earlier check of the laptop is dated after
  its clock.
- **A laptop's row is built from all its runs (`Merge-LaptopRuns`).** It is shown from the latest
  finished run, else the latest. A physical "problem" stands until a finished check known to be
  later answered "ok"; a skip never erases it. A run's scan findings stay until a finished check
  known to be later scanned again, its drive faults until such a check read the drive. Carried
  reasons are tagged with the check that found it, and de-duplicated with their numbers masked. A
  carried stale finding is the safe failure; a finished recheck made after it on the stick replaces it.
- **A check is complete once its last question is answered**, before the optional remark, and
  `kit.json` is written the moment the work folder exists, so even an empty-looking run names its
  laptop. A folder with no real `kit.json` ranks by its contents alone.
- **Ctrl+C while the app is still scanning keeps the work folder**, which the app goes on writing
  its report into, and says so; the summary then reads the folder.
- **The battery question decides by its answer, not by the stage.** A laptop whose battery was not
  answered is CHECK AGAIN, unless the questions were skipped on purpose or the zip predates KIT-007.
- **The question loop stops if its input ends** (`Read-Host` returns `$null`), instead of spinning
  for ever.

**Verified for real on the T470p.** Three runs, one for each case in "How to verify", behaved as
listed there after each round, and so did a window closed at the first question. There are 140 new
kit checks (208 in all). The summary's checks build 49 lettered states on disk, one per fault the
reviews proved, and run the summary in a child process. Against the kit as committed before
KIT-007, the run fails and aborts; against the second round's kit 38 checks fail, against the
third's 28, against the fourth's 17 and against the fifth's 5, every new case among them. The three pilot zips read the
same as before, every column.

**Date.** 2026-10-26.

---

## TOOL-004 — "Argument types do not match" from `@($Runs)` in Windows PowerShell 5.1

**Symptom.** While fixing KIT-007 the fleet summary died with "Argument types do not match", a .NET
`ArgumentException`, on the line `foreach ($run in @($Runs))` of a new function.

**Root cause.** In Windows PowerShell 5.1, `@($x)` throws that exception when `$x` is a
`System.Collections.Generic.List[object]` made with `New-Object`, wherever it is: a local variable,
a function argument or an object's property. `$b = New-Object System.Collections.Generic.List[object]; @($b)`
fails at script scope. `foreach ($run in $b)` works, so does a pipeline inside the subexpression,
`@($b | Where-Object ...)`, and so does a list made with `::new()`. The kit's existing code had only
ever used the pipeline form on such lists. (My first entry here said it happened only to function
arguments; the fourth KIT-007 review found the wider trigger.)

**The wrong first guess.** That some property of the runs had the wrong type, since the message
sounds like a type mismatch in the data.

**How to verify.** The probe above, in `powershell.exe` (5.1).

**Fix.** `foreach ($run in $Runs)` in `Group-AuditRuns`, with a comment pointing here. The kit
checks cover the function with both arrays and the summary's `List[object]`.

**Date.** 2026-10-26.

---

## TOOL-003 — Scripted answers through a .NET pipe arrived as "∩╗┐n"

**Symptom.** While testing KIT-007, the kit ignored scripted answers. "n" then "case bulges" both
drew "Please type y, n or s.", and nothing was saved.

**Root cause.** `ProcessStartInfo.RedirectStandardInput` gives a `StreamWriter` whose encoding is
the console's (UTF-8 here) and which is created with AutoFlush on. Turning AutoFlush on flushes at
once, which writes the UTF-8 byte-order mark into the pipe before a single line is sent. PowerShell
reads its input in the console's OEM code page, where EF BB BF are the characters ∩╗┐. So the first
line read was "∩╗┐n". Writing raw bytes to `BaseStream` does not avoid it: the mark is already in
the pipe.

**The wrong first guesses.** That `Trim()` would remove the mark (it keeps U+FEFF), and then that
skipping the writer would. A probe script printing the code points of what `Read-Host` received
settled it: 2229 2557 2510 006E.

**How to verify.** In a probe script, have `Read-Host` print the code points of what it received,
and feed it one line through a .NET pipe.

**Fix.** Tests feed answers the way a console does, with no .NET writer: `cmd /c "(echo n&echo
note&powershell -NoProfile -Command Start-Sleep 900) | powershell -File audit-laptop.ps1"`. The
sleep keeps the pipe open, so the kit really waits at the next question. A real keyboard never sends
the mark, so the kit itself was left unchanged. A "fix" in the kit was written and then reverted,
because its comment would have described a problem no person can hit.

**Date.** 2026-10-26.

---

## KIT-006 — Every progress line on the laptops read "System overview ù running"

**Symptom.** The owner photographed the kit's window during a laptop check on 2026-10-26. Every
progress line had a `ù` where the em dash belongs: `[1/15] Security posture ù running`. The scan
itself was unaffected, but the same garbling was in every result zip's `console-output.txt`.

**Root cause.** The app is a WinExe and has no console of its own, so .NET encodes its redirected
output in the ANSI code page (1252), where the em dash is byte `0x97`. `Show-NewLines` in
`audit-laptop.ps1` decoded the file with `[Console]::OutputEncoding`, the OEM code page (437 on the
laptops), where `0x97` is `ù`. `collect-results.ps1` read it as ANSI by default, so it happened to
be right on English Windows, by luck rather than design.

**The wrong first guess.** It was mine, and it is written into the 2026-10-23 code. I assumed the
app, started with `-NoNewWindow`, shares the console and so writes in the console's code page. That
holds for a console app, but not for a windowed one. There is also a tempting second wrong guess:
CLAUDE.md warns that a literal em dash in a *kit script* is garbled on some laptops. The kit
scripts are pure ASCII, though, and the byte came from the exe.

**How to verify.** Read the raw bytes of a `console-output.txt`. Before the fix, all three laptop
zips and the T470p had `0x97` and no `E2 80 94`. After it, the T470p had 29 UTF-8 em dashes, no
`0x97` and no BOM, and the file decodes strictly as UTF-8.

**Fix.** `HeadlessScan` sets UTF-8 (no BOM) on stdout and stderr before writing anything, so every
character survives on every Windows language, Vietnamese included. It also catches a crash and
writes the report itself, through that writer, then exits with 1. A review agent proved that the
runtime's own crash text bypassed the writer: with a Vietnamese letter in the output path, it
printed `?` for that letter and exited with `0xE0434352`. All the readers decode UTF-8:
the kit's live display, `collect-results.ps1`, and the daily smoke. The smoke decodes the real scan
output strictly every day and fails if the em dash does not survive. Six kit checks hold the writer,
the readers and the crash path together, because they live in different files. Zips from the earlier
`af47a36` and `462f176` builds still hold ANSI text, so decode those as code page 1252.

**Date.** 2026-10-26.

---

## KIT-005 — The fleet table sent a laptop for a drive check over errors on two USB drives

**Symptom.** On 2026-10-26 the pilot's zips were reviewed again on the T470p. One ThinkPad T470p
carried the reason *"Disk errors in the event log, check that drive: Disk I/O errors (90 days)"*.
The report's own Detail line said where the 47 errors came from: *"on the disk Windows runs from
(disk 0): 0; on other drives: disk 1: 34, disk 2: 13"*. In that zip's `disks.json`, disks 1 and 2
are both on USB: the audit drive, and a laptop hard disk in a USB caddy. The laptop's own SSD had
logged none.

**Root cause.** The fleet row turned the app's grade of `events.disk_errors_90d` straight into a
reason. The app grades the total for every drive Windows has seen, and keeps the per-drive split
only in the Detail text. KIT-003 filtered USB drives out of the *drive health* grade but left this
line alone.

**The wrong first guess.** KIT-003's entry says `audit-common.ps1` "already excluded
`events.disk_errors_90d`" for this reason. That was true of the drive-health grade only. The
separate event-log reason, added on 2026-10-23, still fired on the total. The comment was read as
covering the whole metric, so the 2026-10-25 review did not look at it again.

**How to verify.** In a result zip, read the Detail of `events.disk_errors_90d` in
`report/pc-health-report.json`, and look up each disk number it names in `disks.json`. A reason
may count only the disk Windows runs from, disks attached now on an internal bus, and errors
tied to no disk.

**Fix.** `Get-DiskErrorSplit` and `Get-DiskErrorsAgainstLaptop` in `audit-common.ps1` read the
split, which is fixed English from `DiskEventAttribution.Describe`.

- **Excluded, but printed as a note:** errors on USB drives, and on disk numbers not attached
  during the audit.
- **Still counted:** errors tied to no disk, following KIT-003's rule that unknown stays in.
- **The whole total still counts** when anything is missing: no split in the Detail, a split that
  does not add up to the total, no Detail field (the JSON omits empty ones), or no drive list.
  An unreadable sentence must never make errors disappear.
- **The threshold** (`$script:DiskErrorsNormalMax` = 5) mirrors `rule.storage.disk-events`, and a
  test fails if the two disagree.

There are 17 new kit checks, and 11 of them failed against the old code. The other 6 are the
fallback cases, where counting everything is the old behaviour kept on purpose. All 62 pass. Old
zips are read correctly, because the split has been in the Detail since HW-001 (2026-10-21).

**Date.** 2026-10-26.

---

## PKG-004 — Adding the kit's tests to the build made the build fail to package

**Symptom.** From 2026-10-25, `build-release.ps1` failed at the Package step with
*"audit-common.ps1 ... is being used by another process"*, naming a file it had copied into the
staging folder seconds earlier. Three runs in a row, each naming one of the kit's `.ps1` files.

**Root cause.** Self-inflicted, by the commit that added the audit kit's PowerShell tests to the
build. `-SkipTests` packaged cleanly every time, which isolated it. Running a PowerShell script
suite leaves Windows' script scanning warm, and it opens the freshly copied `.ps1` files just as
`Compress-Archive` reaches them. Nothing is wrong with the inputs; the step is racing a scanner.

**How to verify.** Run `build-release.ps1` twice, once plain and once with `-SkipTests`. The lock
only appears in the run that executed the tests, and the file is openable again moments later.

**Fix.** The Package step retries up to five times with a growing wait, prints the reason each
time it waits, and still throws if the lock never clears. Deliberately not silent: a retry nobody
can see is how a real lock gets mistaken for a slow disk.

**Lesson.** A test step added to a build can break a later step without touching its inputs. The
first two retries of the failing build were run on the assumption it was a flake; it was
reproducible, and the thing that proved it was comparing against `-SkipTests` rather than
retrying harder.

**Date.** 2026-10-25.

---

## REP-001 — Double.MaxValue stood in for "never scanned", and printed 309 digits in the report

**Symptom.** Two of the four laptops in the first fleet audit carried this in
`report/pc-health-report.json`:

```json
{ "Id": "malware.quick_scan_age_days", "Value": { "Number": 1.7976931348623157e+308 },
  "Detail": "No quick scan has ever been recorded on this PC." }
```

**The first diagnosis was wrong, and the fault was worse than recorded.** It was written up as
machine-readable-only, on the grounds that the HTML suppressed the number. It does not.
`HtmlReportExporter` renders the value with `"0.##"`, which turns `double.MaxValue` into a
**309-digit** number, and both of those laptops' HTML reports show it in the value cell. The
first search for it looked for `1.79769...e+308` and found nothing, because the rendered form has
no decimal point and no exponent — a search that confirmed the comfortable answer.

**Root cause.** Defender reports "this never happened" as `uint.MaxValue`. The module turned that
into `double.MaxValue` purely so the rule's catch-all `Bad` band would fire. `MetricValue` has
carried exactly one of Number / Text / Flag all along, and the full-scan metric three lines below
already used the text `"never run"` for the same situation, so the sentinel was never needed.

**Fix.** `ScanRecency` in `Security/DefenderScan.cs` — one pure place that produces all three
answers (a real age, "never run", and "Defender did not report it"), so the sentinel cannot come
back and the could-not-read case stops borrowing the never-ran wording. `malware.json` grades the
text explicitly instead of falling through to the catch-all. Tests lock both the module output and
the serialized shape at the exporter, so a future metric smuggling a sentinel down the same path
is caught there rather than in a customer's report.

**Date.** 2026-10-25.

---

## SMART-001 — The SMART check blamed administrator rights on a scan that had them

**Symptom.** On a ThinkPad T470s the scan ran elevated (`RanElevated: true`), and `disk.smart`
still failed with *"This usually means the scan is not running with administrator rights."* The
real reason was that the drive is NVMe, which has no ATA SMART attribute table at all. The health
of that drive was already in the same report, read by `disk.reliability`, which completed.

**Root cause.** The sentence was a constant. The module asserted a cause it had never checked,
though both facts it needed were to hand: `context.IsElevated`, and `MSFT_PhysicalDisk.BusType`,
which two other checks already read.

**Fix.** `SmartAvailability` diagnoses the refusal from those two facts and returns one of three
answers, each with its own wording. An NVMe-only machine is now `Skipped` — a fact about the
hardware, not a check that broke — which keeps the "some checks could not finish" banner off the
healthy modern laptops that are most of the fleet.

**The direction of the bus list is the whole safety property, and the first draft had it
backwards.** It listed the buses that *can* answer, so a bus number the code had never met fell
outside the list and counted as "nothing to read here" — turning an unread SATA disk into a
silent pass. That is this project's four-times-shipped bug, and it was caught by review rather
than by a test. The list now names the buses known to have *no* ATA SMART table
(`NoAtaSmartBusTypes`), so anything unfamiliar counts as a drive that should have answered and
the check stays failed. `[InlineData(250)]` exists to hold that line.

Skipping does not let an unread drive pass as healthy: the module emits no metric either way, and
when `disk.reliability` comes back empty too, the fleet row says "No drive inside this laptop
reported its own health, so drive health was NOT checked" (KIT-003).

**Deliberately not done: softening the fleet row.** The review also proposed a change in
`audit-common.ps1` so that "Did not finish: Drive SMART health" became a note rather than a reason
whenever the wear counters had read the drive. Once the app fix landed, that turned out to be
both unnecessary and wrong. Unnecessary, because an NVMe-only laptop now returns `Skipped`, and
the fleet row only raises a recheck for `Failed` or `PartiallyCompleted` — so the reason it was
meant to suppress no longer appears. Wrong, because the cases where SMART still *fails* are the
ones where a drive that could have answered did not: a SATA disk behind Intel RST, a controller in
RAID mode, an unelevated scan. In every one of those the drive genuinely went unread and the
manager should be told. The proposed gate was `$driveRead`, which never asked *why* SMART failed,
so it would have softened exactly those. Fixing the diagnosis in the app removed the need to
weaken the verdict in the kit.

**Sibling, same class.** `VolumesModule` carried the identical unchecked diagnosis for TRIM —
"unavailable without admin, it is covered by the elevated scan" — in a module that is not an
admin module and may already be running elevated, in which case both halves of that sentence were
false and a genuinely broken query read as a deliberate deferral. It now asks before it says.

**Date.** 2026-10-25.

---

## KIT-004 — A scan that took three minutes was recorded as lasting -316 seconds

**Symptom.** One result zip from the first fleet audit had `ScanSeconds: -316`, with `FinishedAt`
a minute *before* `StartedAt`. The operator was shown "Finished in -316 s" on screen.

**Root cause.** Every elapsed figure in the kit was two wall-clock readings subtracted. A laptop
that has been switched off for months comes up with a stale clock and Windows steps it as soon as
it finds a network — here, mid-scan, by roughly seven minutes. `Get-Date` is not monotonic. Every
old laptop in the fleet is a candidate, so this was going to recur.

**How to verify.** In `kit.json`, compare `FinishedAt - StartedAt` against `ScanSeconds`; a gap
between them is the step the clock took.

**Fix.** A `Stopwatch` measures elapsed time, so the figure cannot go backwards whatever the clock
does. The step itself is no longer hidden: the wall-clock span less the stopwatch span is recorded
as `ClockJumpSeconds`, and past 120 seconds both the operator and the fleet table are told in one
sentence. It is a note and never a recheck — a laptop is not re-audited because Windows fixed its
clock, and the health readings are unaffected.

Two things found on the way. `CheckedAt` decides which run of a serial the manager sees; it now
prefers the finish time when the clock was stepped, since the start was read before the step. And
the timestamps were being formatted with the current culture, where `:` is the *time separator*
placeholder, not a literal — under fi-FI the same format writes `16.51`, and these strings are
compared as text to pick the latest run. Both now use the invariant culture.

Deliberately not changed: the zip's file name. It is chosen before the scan starts, which is
before anything can know the clock is about to move, so no care at that point helps — which is
exactly why the sentence shown to the operator says the file name may be a few minutes out.

**Tests.** The kit suite grew to 45 checks, including one that reads `audit-laptop.ps1` and fails
if any elapsed time is ever computed from two `Get-Date` readings again.

**Date.** 2026-10-25.

---

## KIT-003 — The fleet table told the manager to replace drives that were not in the laptops

**Symptom.** The first real fleet audit (2026-10-25, reviewing four result zips from three
ThinkPads) produced, for a T470s: *"Replace the drive: Uncorrected read errors (PHD 3.0
Silicon-Power — 1000 GB)"*. That drive is the external USB disk the audit itself was running
from. The laptop's own NVMe SSD had no fault at all. The same false line appeared on a second
laptop, where it sat beside a real fault on the internal SATA drive and was indistinguishable
from it.

**Root cause.** `Get-AuditRow` took the worst grade across *every* drive the scan saw.
`audit-common.ps1` already excluded `events.disk_errors_90d` for exactly this reason — its
comment reads "they can come from a USB drive" — but the per-drive error counters and SMART
metrics were not filtered at all. The bus type was sitting in `disks.json` the whole time and
nothing read it.

`AUDIT-GUIDE.md` tells the operator to unplug every other USB drive, and that instruction can
never cover this case: the kit runs *from* a USB drive, so one is always attached. An operator
cannot follow their way out of this, which is why the filter had to be in the code.

**How to verify.** In a result zip, compare `disks.json` (`BusType`) with the `Subject` of every
`storage.*` and `smart.*` verdict in `report/pc-health-report.json`. Any subject whose drive has
`BusType = USB` must not appear in the row's drive-health reasons.

**Fix.** `Get-ExternalDriveNames` and `Test-ExternalDrive` in `audit-common.ps1`: drive health is
decided only by drives whose bus is not USB/1394/SD/MMC. Three details that matter more than the
filter itself:

- A drive whose bus cannot be positively identified as external is still graded. Missing a
  failing internal drive sends a bad laptop to an employee; condemning a good drive only wastes
  money. The cheaper mistake is the one to make.
- A scan that read nothing but the audit drive now reports drive health as **Not read**, not
  Good, and raises a recheck. Filtering readings away must not look like a clean bill of health —
  this is the project's recurring defect class, and the filter is exactly the kind of change that
  reintroduces it.
- The excluded readings are still printed as a note, because a fault on the auditor's own drive
  is worth knowing about — just not against the laptop.

Matching is on the drive name plus the em-dash separator, never the name alone, so "Samsung SSD"
over USB cannot excuse "Samsung SSD Pro" inside the machine.

**Tests.** The audit kit had **no tests at all** before this. `tests/audit-kit/kit-tests.ps1`
(plain PowerShell, no Pester, so it runs on the same 5.1 the kit targets) now covers both
directions, and `build-release.ps1` and CI both run it, so a kit that grades laptops wrongly
cannot be packaged. Verified red before green: 9 of its 20 checks fail against the code as it
was.

**Date.** 2026-10-25.

---

## KIT-002 — A battery reading 109.7% of design was graded Good

**Symptom.** In the same audit, a nine-year-old T470s reported a full charge of 25,800 mWh
against a 23,510 mWh design capacity — 109.7% — and the app graded it **Good**. The fleet table
showed "109.7%", telling the manager that laptop's battery was in better condition than new. The
reading was taken while the pack sat at 2% and charging, which is when the estimate is least
trustworthy.

**Root cause.** `battery.json` grades `battery.capacity_pct` with `Good >= 80` and no upper
bound, so any nonsense above 80 reads as healthy. A pack cannot hold more than it was built to
hold: a figure past that means the gauge has lost its calibration and is not measuring wear at
all. Both the Windows and the Linux module derive this metric from the same ratio, so the same
reading from a Linux laptop would have been graded Good too.

**How to verify.** `powercfg /batteryreport` and compare `FullChargeCapacity` with
`DesignCapacity`; any ratio over about 105% is the gauge, not the battery.

**Fix.** `BatteryCapacity` in `PcHealth.Core.Metrics` — shared, because the bug had a sibling in
the Linux module and fixing one would have left the other. Above 105% the metric is emitted
`InfoOnly`, so it is still *shown* (nobody should think the check was skipped) but no rule can
grade it, and the module returns `PartiallyCompleted` so the scan says wear was not established.
The kit shows "Not reliable" rather than the number and asks for a recheck.

The margin is 105%, not 100%: a fresh pack routinely measures a shade over design, and refusing
to grade those would fire the "some checks could not finish" banner on healthy laptops until
nobody read it any more. Both directions are tested, in `BatteryCapacityTests` and in the Linux
module's fixture tests.

**Date.** 2026-10-25.

---

## W11-001 — Windows 11 readiness said "System.String[]" and gave TPM advice to a processor block

**Symptom.** The first live scan with the new Windows 11 check (2026-10-23, ThinkPad T470p)
reported `os.win11_blockers = "System.String[]"`. It then explained the block as an unknown code
and advised switching on TPM and Secure Boot, which no setting can do for a processor block.
All unit tests had passed.

**Root cause.** `RedReason` in the appraiser's registry key is a REG_MULTI_SZ, which .NET returns
as `string[]`, and `Convert.ToString` of an array is its type name. The unit tests were built from
values copied out of PowerShell, and PowerShell prints an array's items, so a plain string looked
right.

**How to verify.** `(Get-Item 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\TargetVersionUpgradeExperienceIndicators\GE25H2').GetValueKind('RedReason')`
prints `MultiString`.

**Fix.** `Win11Readiness.RegistryText` joins string arrays with spaces and formats numbers
invariantly; a test covers the multi-string case. A live re-run shows `CpuFms`, "the processor is
not on Microsoft's supported list", and "a processor block cannot be fixed by any setting".
Lesson: test data copied through PowerShell's display is not the data .NET receives; a live run
on a real machine is part of the test.

**Date.** 2026-10-23.

---

## KIT-001 — `collect-results.ps1` passed the report path unquoted

**Symptom.** None reported; found while writing the audit kit on 2026-10-23.

**Root cause.** `Start-Process -ArgumentList` joins the array with spaces and adds no quotes. The
report folder is under `%TEMP%`, so on a laptop whose Windows user name contains a space
(`C:\Users\Nguyen An\...`) the app would receive `--out C:\Users\Nguyen` and write the reports
somewhere else. The zip would then arrive without them. `daily-smoke.ps1` already quoted the path,
because it was written later.

**How to verify.** Run the old script under an account with a space in its name: the zip holds no
`report` folder.

**Fix.** The path is passed as `"`"$reportDir`""`, in `collect-results.ps1` and in the new
`audit-laptop.ps1`.

**Date.** 2026-10-23.

---

## TEST-001 — The daily smoke would have read only one of two test projects' results

**Symptom.** None yet; found by reading the script on 2026-10-23 while adding the second test
project (`PcHealth.Tests.Linux`). The Tests step ran `dotnet test` on the solution with
`--logger trx;LogFileName=tests.trx` and read `Get-ChildItem *.trx | Select-Object -First 1`.

**Root cause.** With a fixed `LogFileName`, every test project writes the same file name, and the
step read only the first results file it found. With two projects, the row would have shown one
project's count, and a failure in the other could have passed as PASS: the rule-7 fault, in the
script that checks for it.

**How to verify.** A smoke run's Tests line must count every project: on 2026-10-23,
`444/446 passed, 2 skipped` (381 Windows + 65 Linux, the 2 skipped being the Linux-only live
tests). Fewer results files than `tests\**\*.csproj` now fails the step outright.

**Fix.** `LogFilePrefix=tests` (one file per project), every file read and the counters summed,
skipped tests shown separately. The compiled privacy gate also names
`PcHealth.Modules.Linux.dll` explicitly, since that assembly is not in the Windows app's folder.

**Date.** 2026-10-23.

---

## LNX-001 — A metric value followed the regional format: "4,0 GB" of swap

**Symptom.** The first run of the Linux fixture tests on this laptop failed:
`Expected: "4.0 GB, 25% in use"`, `Actual: "4,0 GB, 25% in use"`. The laptop's regional format
was still Vietnamese from the J3 acceptance test.

**Root cause.** A text value built with string interpolation (`$"{kb / 1048576.0:0.0} GB"`)
formats with the current culture. The analyzers that are errors here (CA1305) do not flag
interpolation, so nothing stopped it. Values are invariant everywhere else in the report; only
Details follow the regional format.

**How to verify.** Run the tests under a non-English regional format: the swap test fails without the fix.

**Fix.** `string.Create(CultureInfo.InvariantCulture, $"...")` for that value. The other
interpolated values in the Linux modules hold only whole numbers or text, which cannot change
with the culture. Caught before it was ever committed.

**Date.** 2026-10-23.

---

## TOOL-002 — Git Bash's `grep -c $'\r'` reported no CRLF in files that had it

**Symptom.** Before scripted edits on 2026-10-23, `grep -c $'\r'` returned 0 for the rule files,
so the edit script matched lines ending in `\n`. It found nothing to change: the files were CRLF.

**Root cause.** This repository is checked out with `core.autocrlf=true`, so the working tree is
CRLF while the repository is LF. Git Bash's `grep` handles the CR as part of the line ending, not
as a character it can count.

**How to verify.** `python -c "print(b'\r\n' in open('src/PcHealth.Core/Rules/storage.json','rb').read())"`
prints True.

**Fix.** Edit scripts detect the file's own line ending (`"\r\n" in text`) and write it back
unchanged. Check line endings with Python's byte read, not `grep`.

**Date.** 2026-10-23.

---

## TOOL-001 — Scripted edits from bash turned escape sequences into real control bytes

**Symptom.** On 2026-10-23 a scripted edit meant to write the text `"\0"` (backslash, zero) wrote
a real NUL byte instead, twice: once into `BrowserExtensionsModule.cs` during its own fix, and
once into `NEXT-STEPS.md`. Git then showed the file as `Bin` in `git show --stat` and stopped
diffing it. A `printf`-built commit message hit the same problem, and git refused it with
`a NUL byte in commit log message not allowed`.

**Root cause.** Python or `printf` code passed through a bash heredoc goes through one more layer
of escape processing than expected: `\\0` arrives as `\0`, which Python and `printf` read as NUL.

**How to verify.** `git show --stat HEAD` shows `Bin` for a text file; for a scan, run
`python -c "import subprocess; print([f for f in subprocess.run(['git','ls-files'],capture_output=True,text=True).stdout.split() if bytes([0]) in open(f,'rb').read()])"`.

**Fix.** Both bytes were replaced, and no tracked text file contains a NUL. For scripted edits, build
backslashes and control bytes from their character codes (`bytes([92])`), or use an editor
tool rather than a shell, in file content and commit messages alike.

**Recurred the same day**, in a Python edit of `daily-smoke.ps1` passed through a quoted heredoc:
the Windows path `src\\PcHealth.Modules.Linux\\bin\\Release\\net10.0\\…` arrived with `\b` and `\n`,
which became a backspace byte and a line break. The control-byte scan of every changed file
(any byte below 32 other than tab, LF and CR) caught it before anything ran. The line was rebuilt
from `bytes([92])`. The rule stands: **no backslash in any script text that passes through a heredoc**.

**Date.** 2026-10-23.

---

## PKG-003 — `build-release.ps1` wrote the release to the root of the drive

**Symptom.** On 2026-10-22, `powershell -File .\build-release.ps1` printed `\release\PcHealthCheck-…zip`,
and the files appeared at `D:\release`, outside the project. The tests and checksum all passed,
so nothing looked wrong except the path.

**Root cause.** The parameter default was `"$PSScriptRoot\release"`. Windows PowerShell 5.1 leaves
`$PSScriptRoot` empty while parameter defaults are evaluated, so the default became `\release`,
which is the root of the current drive. In the script body `$PSScriptRoot` is set, which is why the
build itself found the project. CI uses PowerShell 7 and never showed it. The 2026-10-17 backup
build was run another way.

**How to verify.** Run it from Windows PowerShell 5.1 (`powershell`, not `pwsh`) and read the last
two output lines: they must start with the project folder.

**Fix.** The default is set in the script body (`if (-not $OutputDir) { … Join-Path $PSScriptRoot 'release' }`).
Confirmed with a 5.1 rebuild: output in `<repo>\release`, no `D:\release` created. The stray
`D:\release` was moved into the project.

**Rule this leaves behind.** In a script meant for Windows PowerShell 5.1, never use
`$PSScriptRoot` in a `param()` default. `daily-smoke.ps1` and `collect-results.ps1` use it only in
the body and are unaffected.

**Date.** 2026-10-22.

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

**Fixed 2026-10-21.** All four, plus the drive attribution and the history line below. Locked by
`HardwareAuditFixTests` and `ReportExportTests`, and confirmed on the T470p with an elevated scan:
Memory card Bad (4 low-memory warnings), Sensors Bad (processor zone 96 °C), both USB drives
reported as not read, and Windows 10 supported until 2027-10-12. A Seagate hard disk in a USB caddy
showed a sixth case of the same fault, "SSD endurance used 0%, Good", because the caddy reports
it as an SSD with spindle speed 0. Over USB, 0% wear is no longer graded
(`ReliabilityCounterCheck.WearIsTrustworthy`).

**The plan, as originally proposed:** (1) Move the ESU end date to 2027-10-12 and keep it in data, not
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
