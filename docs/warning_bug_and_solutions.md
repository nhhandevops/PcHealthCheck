# Warnings, bugs and solutions

Faults found while working on this project, and what actually fixed them. Newest first.

Each entry: symptom → root cause → how to verify → fix → date.

Bugs found by *running* the app on real hardware live in `HANDOFF.md` alongside the design
notes they changed. This file is for the ones that would otherwise be forgotten: build,
packaging and tooling faults that no unit test covers.

---

## PRIV-003 — A drive the SMART check could not match was named by its device path

**Symptom.** Found by the Step A gate on 2026-10-28, by reading the code. No report has been seen
to carry it. `DiskSmartModule.Resolve` names each SMART row's drive by matching the WMI
`InstanceName` with `Win32_DiskDrive.PNPDeviceID`. When no inventory row matched, it used the
`InstanceName` itself as the drive's name, that is, as the row's Subject in the report, the JSON
and the history. For a USB disk that device path can contain the disk's serial number. Meanwhile
PRIVACY.md said "the app itself still never reads a serial number".

**Root cause.** The fallback treated a device path as a harmless label. It was written before the
plan's principle 10 ("never serials") existed, and nothing tested the unmatched path.

**How to verify.** Read `DiskSmartModule.Resolve`. An `InstanceName` that matches no
`PNPDeviceID` takes the fallback. Before the fix the Subject was the path; now it is
`Drive not in Windows' disk list (<6 hex>)`.

**Fix (2026-10-28).**
- An unmatched drive is named by the first 6 hex digits of the SHA-256 of its device path. That
  is stable across scans, so its history still lines up, and it cannot be turned back into the
  path.
- PRIVACY.md now says what is true: the app reads the device path in memory to link its readings,
  and never reports it.
- No unit test: `Resolve` is private, and the Windows modules have no `InternalsVisibleTo`. The fix
  is three lines and is checked by reading.

**Date.** 2026-10-28.

---

## SEC-002 — A drive encrypted with its key in the clear read "not encrypted"

**Symptom.** Found while planning the company image (GOLDEN-IMAGE-PLAN I1, 2026-10-28).
`SecurityPlatformModule` queried BitLocker's `ConversionStatus` and graded only `ProtectionStatus`.
Protection 0 is Microsoft's "The volume is not encrypted, partially encrypted, or the volume's
encryption key for the volume is available in the clear on the hard disk"
([Win32_EncryptableVolume](https://learn.microsoft.com/en-us/windows/win32/secprov/win32-encryptablevolume)).
Three different drives therefore all read "This drive is not encrypted":

- a fully encrypted drive with its key in the clear. Windows 11 device encryption leaves this
  state on a PC with only local accounts ("a clear key that is the equivalent of standard BitLocker
  suspended state"), and so does a suspended BitLocker;
- a drive in the middle of encrypting;
- a drive that was never encrypted.

Windows 11 24H2 removed device encryption's DMA and HSTI/Modern Standby prerequisites, so more
laptops can now reach the first state by themselves
([BitLocker overview, Device encryption](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/)).

**Root cause.** The column that tells the three apart was fetched and never used.

**The wrong first guess.** That the key-in-the-clear drive should grade Good, "it is encrypted".
The key sits on the drive beside the data, and Microsoft: "If a device uses only local accounts,
then it remains unprotected even though the data is encrypted." It keeps the grade an unencrypted
drive has, Normal, never Bad, because encryption is not required (REMEDIES-PLAN decision 12). What
changes is that the report says which drive it is.

**How to verify.** `UsbAndDriveKindTests`, I1 section: fully encrypted with protection off says
"encrypted, but its key is in the clear (device encryption waiting, or BitLocker suspended)", on an
internal drive (Normal) and a USB one (information). Encryption in progress or paused, even with
protection reported on, says it is not finished, and decryption says so. Both are Normal. Neither
the Detail nor the tips say "not encrypted" for a drive that is. A drive never encrypted, and one
fully protected, grade as before.

**Fix.**
- `VolumeReadings.Encryption` takes both statuses. Protected and not mid-conversion is Good. Every
  state that does not protect the data is Normal, with a Detail naming the state. Protection off
  with no conversion status says Windows did not say which of the three it is.
- The tips said "This drive is not encrypted" and "To encrypt it". They now say "not protected by
  encryption" and "To protect it ... or resume it if it is suspended".
- An unknown protection status is SEC-001's review round.

**Not verified.** No drive in any of these states was scanned. Unelevated, this PC's BitLocker
class answers "Access denied", and the check needs administrator rights.

**Date.** 2026-10-28.

---

## BAK-001 — File History and OneDrive were read for the administrator, not for the user

**Symptom.** Found by the review of 2026-10-27 (REMEDIES-PLAN H1, Step A row 7). The backup check
needed administrator rights as a whole, so it ran in the elevated helper. The helper runs as
whoever typed the administrator password, and File History and OneDrive folder backup are read
from that account's own profile (`%LOCALAPPDATA%` and `HKCU`). On a company laptop, where a
technician types the password, the report described the technician's backups, not the employee's.
An unelevated scan had no backup rows at all.

**Root cause.** Two of the check's four readings need admin (restore points and shadow copies), and
the module was marked as needing admin as a whole. The per-account readings went with them.

**The wrong first guess.** That restore points and System Protection could move out too. Read
unelevated on the dev T470p on 2026-10-28: the `SystemRestore` class answers "Access denied",
`Win32_ShadowCopy` "Initialization failure", and the `SPP\Clients` key is refused. Only
`SystemRestoreConfig` (and the registry value behind it) can be read. Microsoft documents its
`RPSessionInterval` as the interval between checkpoints, and "zero if System Restore is disabled"
only for Windows Vista ([SystemRestoreConfig](https://learn.microsoft.com/en-us/windows/win32/sr/systemrestoreconfig)).
That is not a System Protection reading, so it is not used.

**How to verify.** `BackupSplitTests`: the new check runs without admin and after the helper, and
the admin check no longer reads File History or OneDrive. One working backup still proves "backed
up somewhere" (Good). All three read and all off is still Bad. Shadow copies the admin check did
not count, or a read that broke, give no conclusion, with a Note. `ScanOrchestratorTests`: a check
marked `RunsAfterOthers` starts after the helper and sees its shadow-copy count. Live, unelevated
on the dev T470p: `backup.files` Completed, File History Normal, OneDrive information only, no
conclusion and the shadow-copy Note.

**Fix.**
- `Modules/BackupFilesModule.cs` (`backup.files`, no admin): File History and OneDrive folder
  backup for the account the app runs as, and the conclusion, "Your files are backed up
  somewhere". The texts say "this account".
- `Modules/BackupPostureModule.cs` (`backup.posture`, admin) keeps restore points and shadow
  copies, renamed "Restore points & shadow copies".
- The conclusion (`Support/BackupProtection.cs`) needs the helper's shadow-copy count. The new
  `ModuleManifest.RunsAfterOthers` runs the check after the helper, one at a time, and hands it
  `ModuleContext.EarlierFindings`. What counts as protection is unchanged, and so is the grade
  (decision 13 concerns only the kit, and the kit does not read these rows).

**Not verified.** No elevated scan was run, so the conclusion drawn from a real helper's count was
seen only in tests. An unelevated scan now shows a File History row, graded Normal when it is off,
as elevated scans already did. The new check is a new consent row in the window, unticked until
the user ticks it.

**Review round, 2026-10-28.** A folder named OneDrive was taken as OneDrive backing it up.
- *Symptom.* OneDrive folder backup was "on" when the Documents, Desktop or Pictures path contained
  the text "OneDrive". Removing OneDrive or signing it out does not move the folders back, so a
  profile left in `%USERPROFILE%\OneDrive\Documents` read "Your files are backed up somewhere: Good"
  with nothing backing it up. So did a local folder such as `D:\OneDriveOld\Documents`. GOLDEN-IMAGE-PLAN
  task 8 removes OneDrive, so on the company image this mostly affects personal PCs. Found by
  reading the code. The heuristic predates Step A, which made it run on every unelevated scan.
- *Fix.* A folder counts only when it is inside the folder of a OneDrive account signed in for this
  account: a subkey of `HKCU\Software\Microsoft\OneDrive\Accounts` (Personal, Business1 and so on)
  whose `UserFolder` holds it (`BackupProtection.OneDriveFolders`). A folder named OneDrive that no
  account holds says so, and is left out of the conclusion. A folder an account holds counts
  whatever its name.
- *How to verify.* `BackupSplitTests`, OneDrive section: inside an account's folder is backed up,
  case and a trailing backslash included. A redirect with no account, a local "OneDriveOld" folder
  and `OneDrive - <org>` beside a Personal account at `OneDrive` are not, and with File History off
  and no shadow copies the conclusion is Bad. On the dev T470p the Accounts key exists with no
  subkeys (OneDrive installed, never signed in), and the folders are where Windows put them.
- *Not verified.* A PC with a signed-in account and folder backup on was not seen. The registry
  layout is OneDrive's own, and no Microsoft page documents it.

**Date.** 2026-10-28.

---

## HIST-001 — "Last 90 days" counts read 9 days of records as 90 clean days

**Symptom.** Found by the review of 2026-10-27 (REMEDIES-PLAN M1, Step A row 6). On the dev T470p,
installed on 2026-10-19, the System event log went back 9 days on 2026-10-28, yet "Blue screens (90
days): 0" read as 90 clean days. The same held for every event-log count, the low-memory warnings
and the disk errors included. After a reinstall, a cleared log or a small log that wrapped, a zero
from a few days of records was graded as an all-clear.

**Root cause.** Each count asked for "the last N days" and never asked how far back the log went.

**The wrong first guess.** That only the event logs were short. Defender's "Threats caught in the
last 90 days" reads a history that Defender itself empties after `ScanPurgeItemsAfterDelay` days,
15 unless changed ([Set-MpPreference](https://learn.microsoft.com/powershell/module/defender/set-mppreference)).
With Defender's defaults that count has always covered 15 days at most. `CrashDumpModule`, named
in the task, has no day window: it reads settings.

**How to verify.** `RecordSpanTests`: a log that covers the window leaves the count exactly as it
was. A log of 9 days says so and its zero is still graded. A zero from 2 days of log is
information only, with one Note per log, and a count above zero keeps its grade. A clock earlier
than the oldest record, or than the app's build, gives an unknown span, and its zero is not
judged. Defender's span is 15 days, or less on a young install. Kit check: a disk-error Detail
ending with the span is still split. Live, unelevated, dev T470p, 2026-10-28: every System-log
count says "records cover only 9 days", app crashes (7 days) unchanged, and Defender's count says
9 days ("Windows was installed 9 days ago"). All grades are as before, and no module degraded.

**Fix.** `Support/RecordSpan.cs`, applied by `EventsModule` (System and Application logs, from
each log's oldest record), `MalwareDefenderModule` (Defender's purge setting, and the install age
when nothing was caught) and `StabilityModule` (its 30-day range). The span is given in days,
never as a date. A zero from under 7 days, or an unknown span, is shown but not graded, with a
Note, not a failure. A log whose span could not be read is a failure (`failures.Record`).

**For the kit (changed in the second review round below).** `audit-common.ps1` reads one of these
Details: the disk-error split of `events.disk_errors_90d`, by regex. The span sentence goes after the
split, and the split still parses. The kit wrote "N in 90 days" itself, which on a short log is the
log's span, not 90 days. It reads no other changed Detail.

**Review round, 2026-10-28.** "Only a zero is excused" assumed every count's Good band is zero.
- *Symptom.* "Application crashes (7 days)" is Good up to 2. On a laptop reimaged the day before,
  the Application log covers 1 day: 2 crashes (about 14 a week) read Good, while a zero from the
  same log was not judged. Found by reading the code.
- *Fix.* `RecordSpan.Apply` takes the top of the count's Good band (`goodUpTo`, 0 by default), and
  `EventsModule` passes 2 for app crashes. A count inside the Good band from under a week of
  records is not judged, with the Note ("a count this low there is not an all-clear"). 3 or more
  still grades Normal. Every other count it is applied to has a Good band of zero, so nothing else
  changes.
- *How to verify.* `RecordSpanTests`: 1 and 2 crashes from 1 day are information only, 3 is Normal,
  and 2 from a 9-day log is still Good. A test holds the rule to Good at 2 and Normal at 3, so a
  band moved in `stability.json` fails it until `EventsModule` moves too.

**Review round, 2026-10-28: the kit's "in 90 days".**
- *Symptom.* The kit read the split but dropped the span. Beside a Detail saying the records cover
  only 9 days, the reason read "Disk errors in the event log from this laptop's own drive: 12 in 90
  days", and a kit check locked that wording in. A laptop reimaged a week ago, with 3 errors in 9
  days, was shown as 3 in 90 days: a tenth of the real rate. "Blue screens in the last 90 days: N"
  did the same. No verdict moved, because the kit's threshold is on the count. Found by reading the
  code.
- *Root cause.* The paragraph above left the kit's wording alone on purpose. The rate is what the
  manager reads, so the count without its span misleads even when the verdict is right.
- *Fix.* `Get-EventLogWindow` in `audit-common.ps1` reads "records cover only 9 days" ("1 day",
  "less than a day") and "how many days the records cover is unknown" from the Detail. The text then
  says "12 in the 9 days the event log covers", "in less than a day of event log" or "in an event log
  of unknown length". A report with no span sentence (an older app, or a log that covers the whole
  90 days) keeps "in 90 days". Blue screens use it too.
- *How to verify.* `kit-tests.ps1`, under the disk-error split: spans of 9 days, 1 day, under a day
  and unknown for disk errors, 9 days for blue screens, and both again with no span sentence. Each
  span check fails on the kit before the fix. The checks with no span pass before and after.

**Date.** 2026-10-28.

---

## SEC-001 — USB drives graded "not encrypted" against the PC, and a wrong claim about Windows Home

**Symptom.** On the pilot reports of 2026-10-23, `security.drive_encrypted` was graded Normal for
every USB volume: three on laptop A, two on laptop C, among them the kit's own stick. The comment
in `SecurityPlatformModule` said Windows Home has no BitLocker provider, so a failed query there
was "the ordinary shape of a Home edition" and only a note. The tip offered "Device Encryption
(Home)" as if every Home PC had it.

**Root cause.** Encryption was graded per drive letter with no question about where the drive is.
The Home comment was never checked against a Home PC.

**The wrong first guess.** The comment itself. Laptop A runs Windows 10 Home 22H2, and its elevated
scan returned an encryption row for each of its four volumes. Home lacks the switch that turns
BitLocker on, not the provider that reports it. Device encryption works on Home only on hardware
that supports it ([Device encryption in Windows](https://support.microsoft.com/en-us/windows/device-encryption-in-windows-cf7e2b6f-3e70-4882-9532-18633605b7df)).

**How to verify.** `UsbAndDriveKindTests`: an unencrypted USB drive is information only; the
internal drive is Normal unencrypted and Good encrypted, as before; the tip names
`ms-settings:deviceencryption` and Pro, and no longer "Device Encryption (Home)".

**Fix.**
- A USB volume's row is information only, by the D1 rule (`VolumeReadings.Encryption`). The drive
  Windows runs from is graded exactly as before, whatever its connection (decision 12 does not
  change the app's grade).
- A failed query now goes through `failures.Record`: no Windows edition explains it any more.
- The tip: BitLocker on Pro, Enterprise or Education; on Home, device encryption only where the
  hardware supports it, opened with Win+R and `ms-settings:deviceencryption` (Microsoft's
  [list of Settings links](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app));
  otherwise the PC needs Pro.

**Not verified.** The check needs administrator rights, and the live scan was unelevated. What
`ms-settings:deviceencryption` opens on a Home PC without device encryption was not seen.

**Review round, 2026-10-28.** A state Windows could not tell was graded "not encrypted".
- *Symptom.* ProtectionStatus 2 is Microsoft's "PROTECTION UNKNOWN ... One potential cause is that
  the volume is in a locked state" ([Win32_EncryptableVolume](https://learn.microsoft.com/en-us/windows/win32/secprov/win32-encryptablevolume)).
  A locked BitLocker data drive therefore read "Drive encryption: Normal", with tips to encrypt a
  drive that already is, and a Detail saying "could not be determined". A missing value was turned
  into 0 (`?? 0`) and read "not encrypted" outright. Found by reading the code. The logic predates
  Step A and moved unchanged into `VolumeReadings.Encryption`.
- *Fix.* Such a row is information only, with the value "unknown", not "no". `SecurityPlatformModule`
  passes the status as read. The conversion status, which this change also brought in, is SEC-002.
- *How to verify.* `UsbAndDriveKindTests.An_encryption_state_windows_could_not_tell_is_not_graded_and_does_not_read_as_no`:
  status 2 with and without a conversion status, a missing value and an undocumented one.

**Review round, 2026-10-28: what the consent rows say is read.**
- *Symptom.* This check's consent row did not name the reading this fix added: which disk each
  drive letter is on, and its connection (`DriveMap.Read`, MSFT_Disk and MSFT_Partition). The
  event-log and volume checks, given the same reading, did name it, and principle 10 of
  REMEDIES-PLAN asks that every reading be named. A pass over every module Step A changed found
  more. The SMART check's row said "Disk model/serial inventory", though its query selects no serial
  and it now reads each disk's media and bus type on every run. The drive-reliability and disk-speed
  rows left out the disk inventory they read. The other-accounts row did not say it reads each
  profile's folder and each Run command (to skip empty ones), and the backup row did not say it
  reads where Desktop, Documents and Pictures are. PRIVACY.md said the install date is read for the
  Windows 11 comparison only when a verdict is found; the System overview reads it on every scan.
  Found by reading the code.
- *Fix.* Each of those Reads lines says what the check reads, and PRIVACY.md with them.
- *How to verify.* `UsbAndDriveKindTests.Every_check_that_reads_how_drives_are_connected_says_so_and_none_claims_a_serial`
  failed on the old rows. `CapabilityHashTests` now holds that a reworded Reads list, like the
  description, leaves the capability hash alone, so nobody is asked for consent again.

**Date.** 2026-10-27.

---

## BENCH-001 — The disk benchmark could write to a drive the same scan called failing

**Symptom.** Found by the review of 2026-10-27 (REMEDIES-PLAN H2). The benchmark skipped a drive
only when Windows' own health flag said Warning or Unhealthy. On pilot laptop A that flag said
Healthy while SMART counted 5 retired sectors, graded Bad. The benchmark writes up to 1 GiB to each
drive, and it once created the very disk errors the app then graded (project rule 4).

**Root cause.** The benchmark could not see what the other checks found. It runs after them, but
nothing handed their results over, and the SMART check runs in the elevated helper, another process.

**How to verify.** `UsbAndDriveKindTests` (H2 section): of two identical drives only the failing
one is skipped; a drive whose firmware predicts failure, or whose number the event log's disk
errors name, is skipped; cable errors, command timeouts and temperature do not stop it.
`ScanOrchestratorTests`: a check that runs alone sees this process's findings and the helper's.
`PipeChannelTests`: a metric's disk number survives the pipe.

**Fix.**
- `ModuleContext.EarlierFindings`: the orchestrator grades every result gathered before the checks
  that run alone, the helper's included (`ElevatedScanOutcome.ResultsOnceFinished`), and hands
  them over.
- `Metric.DiskNumber`: SMART and reliability rows carry Windows' number for the drive, so drives
  are matched by number, never by model name, which two identical drives share.
- `Support/FailingDrives.cs` is the one check for this. A damage reading graded Normal or Bad
  skips the drive, and so do disk errors in the event log that name its number
  (`Support/DiskErrorLog.cs`, the event-log check's own query). A counter that grew in the history
  is already above its Good band of zero, so the history is not read separately.
- A drive whose own health was not read is still measured, with a note: refusing it would switch
  the benchmark off on every scan without administrator rights.

**Not verified.** No benchmark was run, by rule, and no elevated scan.

**Review round, 2026-10-28.** Disk errors that name no drive did not stop the benchmark.
- *Symptom.* A controller reset (storahci, stornvme or iaStor 129) names `\Device\RaidPort0`, and
  Ntfs 55 names the volume (`C:`), not a disk. The event-log check counts both against this PC, so
  12 of them graded "Disk I/O errors" Bad with "BACK UP YOUR IMPORTANT FILES NOW". `FailingDrives`
  kept only errors with a disk number, and the benchmark still wrote up to 1 GiB to the one drive
  inside the PC. Found by reading the code. The reviewer marked it uncertain, because H2 names only
  media errors. Rule 4 decides it: the benchmark must not exercise a drive the same scan calls
  failing, and the scan's own row cannot say which drive it means.
- *Fix.* `FailingDrives.Find` takes the benchmark's volumes and drives. An error that names a
  volume on a drive attached now is placed on that drive (Ntfs 55's first field is `DriveName`, its
  template on this PC says). Any other, `RaidPort` or a volume not there now, keeps the benchmark
  off every drive that is not external, with the reason in the skip note. A deliberate skip, so no
  failure is recorded. USB drives are still measured.
- *How to verify.* `UsbAndDriveKindTests`, H2 section: controller resets skip C: and measure both
  USB drives, and `Share` counts the same errors against the PC. Errors on E: skip only the USB
  caddy, and on `c:` or `C:\` only the internal SSD. A letter or volume path not attached now skips
  the internal drive. Errors naming only USB drives leave C: measured.
  `Disk_errors_in_the_event_log_naming_a_drive_skip_it` locked in the old behaviour, so it now
  checks numbered errors only.
- *Not verified.* No Ntfs 55 or 129 event is in this PC's System log, so the field was read from the
  provider's event template, not from a real event.

**Date.** 2026-10-27.

---

## SMART-002 — An SSD at 40 °C graded on hard-disk bands, and a TRIM row on PCs without an SSD

**Symptom.** Pilot laptop A's SATA SSD read `smart.temperature_c` 40 °C, graded Normal, beside
"Mechanical drives last longest below about 40 °C" (HW-001 noted the text). The reliability
counters' rows said "NVMe SSDs commonly run 50-70 °C" on that SATA SSD and on a hard disk in a USB
caddy. TRIM, which only SSDs use, was asked about and graded on every PC.

**Root cause.** One temperature rule for every SMART drive, written for hard disks.

**The wrong first guess.** Telling an SSD by its SSD-life attribute. Attribute 202, in that list, is
"Data Address Mark Errors" on some hard disks. Windows' media and bus type decide instead.

**How to verify.** `UsbAndDriveKindTests` (H7 section): an SSD at 40 and 70 °C is Good, 75 °C
Normal, 85 °C Bad; a hard disk, a USB caddy and a drive Windows did not describe keep the
hard-disk bands; the TRIM row goes only when every internal drive is a hard disk.

**Fix.**
- An SSD's SMART temperature is `smart.ssd_temperature_c`, graded by `rule.smart.ssd-temperature`:
  Good up to 70 °C, Normal up to 80 °C, Bad above. A typical SATA SSD is rated to work at 0-70 °C
  ([Samsung 870 EVO data sheet](https://download.semiconductor.samsung.com/resources/data-sheet/Samsung_SSD_870_EVO_Data_Sheet_Rev1.1_230509_10129500053000.pdf)).
- A hard disk, a drive behind USB and a drive Windows did not describe keep `smart.temperature_c`
  and its bands, with a detail that says which case it is.
- The reliability counters' temperature detail names the kind of drive.
- No TRIM row, and a note, when every drive inside the PC is a hard disk. A drive that did not say
  what it is, or a list that could not be read, keeps the row.

**Not verified.** SMART needs administrator rights; the live scan was unelevated. The dev T470p has
an SSD, so the no-TRIM case was seen only in tests. The reliability counters' `storage.temperature_c`
is still graded on SSD bands for every drive, hard disks included; only its text changed.

**Date.** 2026-10-27.

---

## STOR-002 — Disk errors logged by USB drives were graded against the PC

**Symptom.** `events.disk_errors_90d` was graded on the total over every drive Windows had seen.
KIT-005 fixed the fleet row, but the app's own report still read Bad for the pilot T470p's 47
errors, all on two USB drives. On the dev T470p: 25 errors, all on USB disk 2, Bad, and the Storage
card Bad.

**Root cause.** The per-drive split existed only as text in the Detail (HW-001); the grade was on
the total.

**The wrong first guess.** That the value could change without touching the kit. The kit checks
that the split adds up to the value. With the value now the PC's share, it would have fallen back to
the app's grade and dropped its note about the USB drives' errors. So the Detail states the total,
"Counted against this PC: 0 of 47.", and the kit reads it.

**How to verify.** `UsbAndDriveKindTests` (H3 section), and 4 new kit checks; the two that use the
stated total fail against the old parser. In the unelevated live scan after the fix:
"Counted against this PC: 0 of 25", Good, Storage card Good. The kit's parser on that report: own
0, USB disk 2: 25.

**Fix.** `DiskEventAttribution.Share` follows the kit's rule (`Get-DiskErrorsAgainstLaptop`), so the
two agree:
- counted: the disk Windows runs from, whatever its connection; a disk attached now on an internal
  or unknown bus; an error that names no disk;
- not counted, but listed: a disk on a USB, FireWire or card-reader bus, and a disk number nothing is
  attached at now (REMEDIES-PLAN H3 says an unknown connection counts as internal; a number with
  nothing attached was treated as the kit treats it, so the app and the kit cannot disagree);
- with no list of attached disks, every error counts, with a note.

The "By drive" sentence is unchanged. `audit-common.ps1` reads the stated total, and reports
without it read as before.

**Not done.** The detailed log that tells "drive disconnected" from "damaged media" (Phase 3), and
"reported through a USB adapter" on drive readings.

**Date.** 2026-10-27.

---

## STOR-001 — A full USB drive read as this PC's disk nearly full

**Symptom.** On all three pilot reports the auditor's USB drive read "Free space 18%", graded Normal,
with the tip to free space on the PC. It was the pilot's only "disk nearly full": the laptops' C:
had 52-82% free. The kit grades only the system drive, so the fleet table was right and the app's
report was wrong. The dev T470p's two USB drives were graded the same way.

**Root cause.** `VolumesModule` graded every fixed volume, and USB drives often call themselves
fixed. Nothing asked which disk a volume was on.

**How to verify.** `UsbAndDriveKindTests` (D1 section): a USB volume at 18% is information only,
with its space and advice in the detail; an internal volume at 5% is still Bad; the drive Windows
runs from is graded even on USB; unknown, RAID and NVMe buses count as internal; SD and FireWire
count as external, like the kit's four. In the live scan the two USB volumes are information only,
and C: and D: are graded as before.

**Fix.** `Support/DiskBus.cs` maps each letter to its disk (MSFT_Partition) and each disk to its bus
(MSFT_Disk), without administrator rights. `Support/VolumeReadings.cs` writes a USB volume's row.
If the map cannot be read, every volume is graded as before, with a note. The sizes in these
details are now written with the invariant culture (REP-002's class, for these lines only).

**Not done.** A USB volume's "Volume health" row, and SMART and reliability readings of USB disks,
are still graded. The kit reads those grades for its "not counted against this laptop" note
(KIT-003), so changing them needs a kit change and the owner's OK.

**Date.** 2026-10-27.

---

## PRIV-002 — The startup checks wrote user names into the report

**Symptom.** Found by the review of 2026-10-27 (REMEDIES-PLAN Step A row 12):
- The admin check `startup.allusers` named the other accounts ("PC\name") in its rows and notes.
  It also wrote each of their Run entries' full command, which usually holds that account's profile
  folder (`AllUsersStartupModule.cs:100,114,121`).
- The unelevated startup check wrote the full path of each unsigned or broken item. An unsigned
  program under `%LOCALAPPDATA%` put the user's folder, and with it the Windows user name, into
  the report.

Both break principle 10 of REMEDIES-PLAN: a report travels when it is shared, and carries no user
names and no command lines.

**Root cause.** Both were written before that rule, for the person reading at the PC.

**How to verify.**
- `UserProfileTests`: accounts are numbered in the order Windows issued them (by the SID's last
  part), the current one included, so "Account 2" names the same account whoever runs the scan.
  Their labels carry no name. An account's items read "Updater [Run]", with no command.
- `StartupSignatureTests`: an unsigned item under `%LOCALAPPDATA%` is written with the token, and
  the real folder does not appear. `ReportPaths` keeps `C:\Program Files (x86)` apart from
  `C:\Program Files`, and writes another profile's folder as `<another account>`.

**Fix.**
- `UserProfile` carries a `Number` and the label "Account N", and the row starts "Yours is
  Account 1." so the reader can place the others. The account's name is no longer looked up at
  all; for a domain account, translating the SID could also have asked a domain controller.
- Other accounts' Run entries are listed by name and key only.
- `Support/ReportPaths.cs` writes every path in the startup rows and notes with `%LOCALAPPDATA%`,
  `%APPDATA%`, `%USERPROFILE%`, `%ProgramFiles%` and similar tokens. PRIVACY.md says so.

**Not verified.** The admin check was not run live: the dev T470p has one account, and the live
check was unelevated.

**Review round, 2026-10-28.** Two faults in the fix above, both found by reading the code.
- *Symptom: whose account is "yours".* The admin check decided which account was the reader's, and
  so which were "other", from the elevated helper's own account. The helper runs as whoever typed
  the administrator password, which the kit asks for on a standard-user company laptop. With a
  technician's password the row read "Yours is Account 1" about the technician's account, listed
  the employee's own as another person's ("These run when that person signs in, not when you do"),
  and neither startup check read the technician's Run keys. Before this fix the names at least
  showed which account was which; numbers do not.
- *Symptom: Winlogon.* A non-default Shell or Userinit value was quoted as found, so a Userinit of
  `userinit.exe,C:\Users\<name>\AppData\Roaming\x.exe` put the user name into the report most likely
  to be sent to IT. The fix above tokenized only the startup rows' file paths, each as one path.
- *Fix: whose account.* The window sends its own account's SID with the request
  (`RunModulesRequest.StartedBySid`), and the worker keeps it only when it is a person's SID
  (`UserProfiles.IsRealUserAccount`). `UserProfiles.Enumerate` marks that profile current, so the
  helper's own account, when it differs, is counted and read like any other. "Yours is" is written
  only when the scan was started from an unelevated window, which runs as the person signed in. An
  app started with "Run as administrator" may run as whoever typed the password, and that is not
  checked, so the row then says "The account that started this scan is Account N", and the total
  "not when the account that started this scan does".
- *Fix: Winlogon.* `StartupReport.Winlogon` builds the row, and `ReportPaths.TokenizeWithin` writes
  every path in the value with its token. A path starts at each drive letter, so the second entry
  of a Userinit list and a path after `cmd /c` are covered too.
- *How to verify.* `UserProfileTests`: enumerated with a made-up starting SID, no profile is current
  and this process's own is listed. The real check, run as the helper started from another account,
  counts this account among the others and writes no "Yours is"; started from this account it does;
  as an elevated app with no SID it names "the account that started this scan". `PipeChannelTests`
  round-trips the SID, and a request without one reads null. `StartupSignatureTests`: a Userinit
  list with a program under `%APPDATA%` is written with the token and without the folder, the
  defaults still read standard, and `TokenizeWithin` handles lists, quotes and `cmd /c`. Each new
  test failed before the fix.
- *Not verified.* No elevated run with two accounts: the dev T470p has one. Consent is not asked
  again: the capability hash covers the id, admin, internet, driver and writes, not the wording.

**Review round, 2026-10-28: what the guide says a zip holds.** Two faults in `AUDIT-GUIDE.md`,
found by reading it against the kit.
- *Symptom: a build day for a kit version.* The guide said that on "a stick built on or after 27
  October 2026", startup paths are written as `%LOCALAPPDATA%\...` and other accounts are numbered,
  so neither carries a Windows user name. The stick built that day, at about 09:55, carries kit
  2026-10-26.1 (app `c067e3a`), which has none of this work: its zips name the user in startup paths
  and name the other accounts. The person carrying the kit could have sent them outside the company
  believing they held no user names. The ESU note was dated the same way (OS-001).
- *Root cause.* The guide dated the change by the day the code was written, not by anything a zip
  records. The person carrying the kit cannot see when a stick was built anyway.
- *Fix.* The guide names the kit version, which the kit prints when it starts and `kit.json`
  records as `KitVersion`: "kit 2026-10-28.1 or later". This work ships in a stick built on
  2026-10-28 or later, so the kit version is bumped from 2026-10-27.1 (never on a stick) to
  2026-10-28.1. `$script:FirstKitWithSupportReadings` in `audit-common.ps1` holds that boundary, and
  the kit's own Windows 11 note uses it too (KIT-008).
- *Symptom: the transcript.* The change above removed the guide's only mention of a user name. But
  every kit, old and new, zips its own log, `audit-transcript.txt`, and Windows PowerShell starts it
  with "Username" and "RunAs User" lines that name the computer and the Windows account. Nothing
  strips them. The kit's own help ("WHAT THE ZIP CONTAINS") and PRIVACY.md's list of what the kit
  reads left it out as well.
- *Fix.* The guide's list, the kit's help and PRIVACY.md now name the transcript and say it names the
  computer and the Windows user the kit ran under, on every kit. Stripping the header before the zip
  is made would be the other way; it was not done, because this round only makes the text true.
- *How to verify.* `kit-tests.ps1` checks that the guide says "kit 2026-10-28.1 or later" (built from
  `$script:FirstKitWithSupportReadings`), never "stick built on or after", and lists
  `audit-transcript.txt` with the user name; that `Test-KitOlder` orders versions by day, then by
  number, and calls no unknown version older; and that this kit is not older than the boundary. Each
  failed before the fix.

**Date.** 2026-10-27; review round 2026-10-28.

---

## START-002 — "Every startup item has a valid signature" beside broken and unverified ones

**Symptom.** Found by the review of 2026-10-27 (`StartupModule.cs:83-84`). The sentence "Every
resolved startup item has a valid publisher signature" was chosen on the unsigned count alone, so:
- with one BROKEN signature it stood beside "Startup items with a BROKEN signature: 1";
- a file Windows could not verify was counted nowhere and read as valid too. That is any status
  the check did not map, for example an untrusted root, or an intermediate certificate missing
  offline;
- `CERT_E_EXPIRED` was mapped to Invalid, so a signature whose certificate expired, with no
  timestamp, would read "BROKEN signature (possible tampering)" and grade Bad.

**The wrong first guess.** That LightShot (certificate expired 2022-05-30) and UniKey (2025) read
BROKEN through that mapping. On the dev T470p WinVerifyTrust, with the app's offline flags,
returned 0 for `Lightshot.exe`. Its signature is timestamped, and Windows judges a timestamped
signature at the time it was made. The mapping only affects an untimestamped one. UniKey is not on
this PC, so whether its signature is timestamped is still unknown.

**Root cause.** The count and the sentence were worked out separately, and only "not signed" had
wording of its own.

**How to verify.** `StartupSignatureTests`, both directions:
- every item verified: the sentence is written;
- a broken, an unverifiable or an expired one: it is not;
- unverifiable: a Note names the item and Windows' answer, and the scan is not degraded;
- the verifier itself unable to run: the scan is degraded;
- nothing could be checked: the counts are left out instead of reading 0;
- unsigned items are still counted and listed.

**Fix.** `Startup/StartupReport.cs` writes the rows.
- Expired is its own state. It gets a Note: "signed by X, but the certificate has expired and no
  timestamp shows the file was signed while it was valid". It is counted neither as signed nor as
  unsigned. The file's digest is checked before its certificate, so expiry is not tampering.
- "Could not verify" gets a Note with Windows' status, and is never counted as verified.
- A verifier that could not run goes through `failures.Record`.
- The sentence is written only when every resolved item verified.

**Not verified.** No file on this PC returns `CERT_E_EXPIRED` or an unmapped status, so those
wordings were seen only in tests.

**Date.** 2026-10-27.

---

## START-001 — A startup shortcut switched off in Task Manager was still listed as starting

**Symptom.** On the dev T470p the all-users Startup folder holds `VpnClient.lnk`, switched off in Task
Manager: its `StartupApproved\StartupFolder` value starts with 05. The startup check still listed it,
and examined its file, as a program that starts with Windows.

**Root cause.**
- All six `StartupApproved` subkeys (Run, Run32 and StartupFolder, in HKCU and HKLM) were read
  into one map keyed by value name.
- Startup folder items were then created with `Disabled: false`, and never looked in the map.
  Their switches are filed by file name with its extension, "VpnClient.lnk", which no lookup used.
- A switch filed for one place, such as HKCU Run, could decide an item of the same name in
  another, such as HKLM's 32-bit Run key.

**How to verify.** `StartupSignatureTests`:
- a StartupFolder switch turns off that shortcut, in its own hive only;
- an HKCU Run switch leaves HKLM Run and Run32 items of the same name alone;
- the odd-first-byte rule, with the values seen on this PC (01 and 05 off, 04 on).

In the live scan of 2026-10-27, a VPN client is no longer among the 8 items.

**Fix.**
- `Startup/StartupApproved.cs` keys each switch by hive, location and name: Run for that hive's Run
  key, Run32 for HKLM's 32-bit Run key, StartupFolder for that hive's Startup folder.
- RunOnce and policy entries have no switch, because Task Manager does not list them.
- The inventory also reads HKLM's 32-bit RunOnce key and both hives' `Policies\Explorer\Run`,
  which it used to skip.

**Not verified.** No 32-bit HKLM Run item on this PC is switched off; the Run32 subkey is empty
here.

**Date.** 2026-10-27.

---

## SIG-001 — Windows Security's own tray icon read as an unsigned startup item on every PC

**Symptom.** All three pilot laptops and the dev T470p reported "Startup items without a publisher
signature: 1", graded Normal. The one item was `SecurityHealthSystray.exe`, Windows Security's tray
icon, started from HKLM Run as "SecurityHealth". A freshly reimaged laptop could never read clean.

**Root cause.** The file has no signature of its own. Microsoft signs a catalog that lists its hash
(`System32\CatRoot`). `AuthenticodeVerifier` asked WinVerifyTrust about the file alone
(`WTD_CHOICE_FILE`), which answers `TRUST_E_NOSIGNATURE` (0x800B0100). Explorer's Digital
Signatures tab shows no signature for the same reason. `Get-AuthenticodeSignature` says Valid,
SignatureType Catalog.

**How to verify.**
- On this PC, WinVerifyTrust with the app's flags returned 0x800B0100 for
  `SecurityHealthSystray.exe` and `notepad.exe`. `Get-AuthenticodeSignature` reports both as Valid,
  Catalog.
- `StartupSignatureTests`: the tray icon verifies through its catalog, signed by Microsoft; an exact
  copy elsewhere verifies too; one changed byte makes the copy unsigned. A plain unsigned file
  stays unsigned, with no failure recorded.

**Fix.**
- A file with no embedded signature is looked up in Windows' catalog database, on the PC:
  `CryptCATAdminAcquireContext2` (SHA-256, then SHA-1) and `CryptCATAdminEnumCatalogFromHash`.
- The catalog that lists it is verified with WinVerifyTrust (`WTD_CHOICE_CATALOG`), with the same
  offline flags.
- "No catalog lists it" (`ERROR_NOT_FOUND`) leaves the answer as unsigned. Any other catalog error
  is a check that could not run.
- The signer's CN and O are now recorded for embedded and catalog signatures alike, from the
  certificate WinVerifyTrust used. This is the groundwork for Step D's allowlist.

In the unelevated live scan of 2026-10-27 the list reads "SecurityHealth (Microsoft Windows)" and
"Lightshot (Kilonova LLC)", and neither is counted as unsigned.

**Review round, 2026-10-28.** A file the catalog hasher refuses was recorded as a broken verifier.
- *Symptom.* For a 0-byte file WinVerifyTrust answers 0x800B0003, not signed, and
  `CryptCATAdminCalcHashFromFileHandle2` then fails with 1006 (ERROR_FILE_INVALID) under SHA-256 and
  SHA-1. For notepad.exe cut to 4 KB the answers are 0x800B0100 and 0x800700C1 (ERROR_BAD_EXE_FORMAT).
  Both went to `CatalogFailure`, and `StartupReport` passes every Failure to `failures.Record`. So a
  Run entry or shortcut pointing at an empty or truncated program, which a failed update can leave,
  put "some checks could not finish" on a healthy PC and made the kit exit 2. Found by reading the
  code, confirmed with a probe on the dev T470p. A 512-byte or text file hashes normally and is
  "not found" (1168), so it already read unsigned.
- *The wrong first guess.* Matching the Win32 codes 1006 and 193. The truncated file's last error
  is already an HRESULT, so the test still failed; the code now compares after `HResultFromWin32`.
- *Fix.* A file the hasher refuses for what it holds is in no catalog, so the embedded "not signed"
  stands. Any other reason the file cannot be hashed, or opened for hashing, makes it Unknown with
  Windows' status: a Note naming the item, not a failure. `CatalogFailure` is kept for the catalog
  database not opening and the catalog lookup failing, which are the check itself breaking.
- *How to verify.* `StartupSignatureTests`: a 0-byte .exe and a 4 KB copy of notepad.exe each read
  NotSigned with no Failure, and the startup report records no failure for them. Both failed before.
- *Not verified.* No other hash error was seen, so the Unknown wording for one was not tested.

**Date.** 2026-10-27; review round 2026-10-28.

---

## REP-002 — Report details follow the regional number format: "4,5–10,0"

**Status: open, found 2026-10-27, not fixed.** Found during the live check for Step A rows 10, 11
and 13, outside their scope.

**Symptom.** On the dev T470p, whose regional format uses a decimal comma, the unelevated scan of
2026-10-27 wrote the `stability.index` detail as "30-day range: 4,5–10,0 (average 6,1)". The value
itself, 6.9, is right: it is a number, not text. Only the detail line is affected, but the JSON and
the kit carry that text, and English is the language of the data (REMEDIES-PLAN, decision 6).

**Root cause.** Interpolated strings with a format, such as `$"{x:0.0}"`, format with the current
culture, and CA1305 does not flag them. LNX-001 was the same fault in the Linux modules. A grep for
`$"...{x:0...}"` without `string.Create(CultureInfo.InvariantCulture, ...)` finds about 30 in `src/`,
among them `StabilityModule.cs:51` and the `memory.commit_pct` detail in `SysOverviewModule.cs`.

**How to verify.** Run a scan on a PC whose number format uses a decimal comma, and search the JSON
details for a digit, a comma and a digit.

**Fix.** Not done. Fix the whole class at once, not this one line: every interpolation that formats
a number into report text goes through `string.Create(CultureInfo.InvariantCulture, ...)`, with a
test that runs a module's formatting under a comma culture. *2026-10-28: the `stability.index`
range line was rewritten for HIST-001 and is invariant now ("4.5–10.0" in the live scan); the rest
of the class is still open.*

**Date.** 2026-10-27.

---

## TIP-001 — Tips sent Windows 10 users to menus that only Windows 11 has

**Symptom.** Found by the review of 2026-10-27. On an English Windows 10 22H2 the tips said:

- "Settings > System > Activation";
- "Settings > System > Troubleshoot";
- "Settings > Windows Update";
- "Settings > Privacy & security > Windows Security";
- "Task Manager > Startup apps";
- in the processor trend advice, "Settings > System > Power".

None of them exists on Windows 10. Activation, Troubleshoot, Windows Update and Windows Security
are under Update & Security there. Task Manager's tab is called "Startup", and the power mode is the
battery icon's slider. Windows 11 has no Update & Security, so Windows 10 paths would be wrong there
instead. The company's laptops run both (decision 8).

**Root cause.** The tips were written from Windows 11's menus, and nothing ever compared them with
Windows 10.

**The wrong first guess.** That the review's list of eight places was complete. Grepping every rule
file for " > " found "Settings > Windows Update" in three more tips (`os.json`, the update-age and
pending-update rules).

**How to verify.** `FreshInstallTests.No_tip_names_a_menu_path_that_only_one_Windows_version_has`
reads every embedded tip and forecast. `The_tips_still_say_how_to_get_there` checks the other
direction: the activation, troubleshooter, startup and Windows Update tips still name a way there.

**Fix.** Each such tip names the page and its `ms-settings:` link, typed after Win+R, which opens
the same page on both versions. The links are from Microsoft's list
([Launch Windows Settings](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app)). Where the step
below the page differs, the tip names both: "Additional troubleshooters (Other troubleshooters on
Windows 11)". Windows Security is opened from the Start menu. The processor advice names the power
mode for each version, after Microsoft's own page
([Change the power mode](https://support.microsoft.com/en-us/windows/change-the-power-mode-for-your-windows-pc-c2aff038-22c9-f46d-5ca0-78696fdf2de8)).
Paths that exist on both were kept: Windows Security's own pages, Control Panel > File History,
Event Viewer and Storage.

**Not verified.** The links were not opened on Windows 11; Microsoft's list marks none of them as
missing there. `audit-kit/audit-laptop.ps1` asks about "Settings > Sound > microphone test", which
is System > Sound on both versions. The kit's text was outside this change.

**Date.** 2026-10-27.

---

## OS-003 — A new Windows install was graded on readings it cannot have yet

**Symptom.** From the review of 2026-10-27: on a new install, Defender's "Days since last quick
scan" reads "never run" and is graded Bad. On the dev T470p, reinstalled on 2026-10-18, the first
scheduled quick scan came 4 days after install. An empty Reliability Monitor history read
"Reliability data collection (RAC scheduled task) appears to be disabled", and the module ended
partially completed, which raises the "some checks could not finish" banner. A freshly installed,
healthy PC must read clean.

**Root cause.** Both readings depend on how long Windows has existed, and the app never asked.

**How to verify.** `FreshInstallTests`, both directions:
- under 7 days, "never run" is shown ungraded with a Note, and an empty history is a Note with the
  module Skipped;
- from 7 days on, "never run" is still Bad, and an empty history still says the check could not
  finish;
- an install date after the clock, or none, is never young;
- a clock set back cannot make an old install look new.

**Fix.** `Support/InstallAge.cs` reads `Win32_OperatingSystem.InstallDate`, and only when one of the
two readings is empty. It compares in UTC, and "now" is never earlier than the support table's
checked-on date. On an install under 7 days old, each reading becomes a Note: "Days since last quick
scan: not judged: Windows was installed 3 days ago. ...". A failed install-date read is a note, and
the reading is then graded as before. The Windows 11 "not assessed" notes now say that Windows runs
its compatibility check by itself about once a day, and to scan again after a day. The kit's CHECK
AGAIN for it is unchanged. PRIVACY.md names the new reading.

**Not verified.**
- **No young install was available.** The dev T470p was installed 8 days before the live check, so
  neither Note fired there; its quick scan read 4 days, Good.
- **A restored disk image keeps the image's install date,** so on the company's reimaged laptops
  this rule will rarely fire.
- **An empty reliability history was not seen.** On the dev T470p the history starts in the hour
  Windows was installed. That half of the fix is a precaution.

**Date.** 2026-10-27.

---

## OS-002 — "Time service not running" was graded on every PC outside a domain

**Symptom.** `time.sync.service_running` was graded Normal whenever W32Time was stopped. On the dev
T470p it is "Stopped", start mode "Manual", and its own detail line already said "'stopped' alone is
not alarming".

**Root cause.** On a PC outside a domain, W32Time is a trigger-start service whose one trigger is
"domain joined" (`sc qtriggerinfo w32time` on the dev T470p). The Time Synchronization task starts
it about once a week (`sc.exe start w32time task_started`) and lets it stop again. Microsoft: "On
workgroup computers that aren't joined to an AD DS domain: the startup value ... is Manual. The
service status is Stopped" and "the default setting can be kept as is"
([W32Time doesn't start on a workgroup computer](https://learn.microsoft.com/en-us/troubleshoot/windows-client/active-directory/w32time-not-start-on-workgroup)).
A threshold every healthy workgroup PC trips is a bug in the threshold, and the company's laptops are
workgroup PCs.

**The wrong first guess.** The original one: explaining the normal case in the detail line while
still grading it.

**How to verify.** `FreshInstallTests.The_time_service_state_is_never_graded`, for stopped and for
running. On the dev T470p after the fix: the row is information only, value false, no rule.

**Fix.** The reading is information only and `rule.time.service` is gone. The detail says how to sync
the clock (Win+R, then `ms-settings:dateandtime`, Sync now). There was no clock-offset grading to
keep: whether the clock itself is right is not checked yet, and the detail says so. That is Step C
row 4 (O5).

**Date.** 2026-10-27.

---

## KIT-008 — With `requireWindows11Ready` on, Windows 11 installed anyway read READY

**Symptom.** Found on 2026-10-27, while decision 9 was turned into code; no audited laptop showed
it, since all three pilot laptops run Windows 10. In the committed kit, with `requireWindows11Ready`
on, a laptop whose processor Windows 11 does not support was REPLACE on Windows 10. With Windows 11
installed or restored onto it anyway, the same laptop read "Already Windows 11" and, with nothing
else wrong, READY. Decision 9 makes that the expected case on the T470s: some get Windows 11 on
hardware it does not support.

**Root cause.** `Get-AuditRow` tested the build first (`if ($build -ge 22000) { 'Already Windows 11' }`)
and read Windows' own verdict only on Windows 10. Running Windows 11 was taken to mean the laptop met
Windows 11's requirements, but Windows 11 installs on hardware its check rejects. The app did not
help either: it read the verdict only on Windows 10 until `os.win11_hardware_supported` was added
the same day (OS-001).

**The wrong first guess.** That the new app reading was enough. The first kit change for decision 9
(same day, not committed) showed it as a note, "Windows 11 on hardware Microsoft does not support",
and never as a reason. Turning the rule back on would then still have passed exactly the laptops
it exists to catch. That change also showed a Windows 11 laptop with no reading as plain
"Already Windows 11", which reads as supported.

**How to verify.** Feed `Get-AuditRow` a bundle with `WindowsBuild` 26200, `os.win11_hardware_supported`
false and `os.win11_blockers` "CpuFms", once with `requireWindows11Ready` true and once false. The
kit checks under "Windows 11 on unsupported hardware" do this in both directions.

**Fix.** The kit reads Windows' verdict whichever Windows runs.
- **With the rule on:**
  - a processor block is REPLACE, worded "Windows 11 runs here, but Microsoft does not support it
    on this processor (CpuFms)" on Windows 11;
  - any other requirement is FIX FIRST;
  - no reading is CHECK AGAIN, as "Not assessed" is on Windows 10.
- **With the rule off (the default since decision 9):** each of these is a note.
- **The column** says "Already Windows 11, hardware support not read" when there is no reading.

**Not verified:** a real Windows 11 laptop. If Windows 11 keeps no verdict for its next version, every
Windows 11 laptop reads "hardware support not read". That is a note with the rule off and CHECK
AGAIN with it on.

**Review round, 2026-10-28.** Two faults in the fix above, both found by reading the code.
- *Symptom: a cause the zip does not show.* Every Windows 11 run without `os.win11_hardware_supported`
  read "Windows has not assessed this laptop for Windows 11". Three kinds of run lack that row
  whether or not Windows assessed the laptop:
  - a zip from an older kit, which never read it on Windows 11. That is every zip so far: the dev
    desktop's of 2026-10-25 (kit 2026-10-23.1), and every one from the stick in use (kit 2026-10-26.1);
  - a run with no report, where Windows 10 read plainly "Not read";
  - a verdict the app does not interpret, where the `os.win11_blockers` row shows Windows did assess it.

  With the rule on, each was CHECK AGAIN, and checking again with the same stick could never clear it.
- *Root cause.* The Windows 11 branch ran before the no-report test, and every missing row was read
  as one cause.
- *Fix.* With no report, the column reads "Not read" whichever Windows runs, and there is no
  Windows 11 text: the no-report reason already says why. Otherwise the text names only the cause
  the zip shows:
  - a blockers row with no verdict row: "Windows gave a verdict the app does not interpret, see its
    report". On Windows 10 the column then reads "Verdict not interpreted", not "Not assessed";
  - on Windows 11, a `KitVersion` older than 2026-10-28.1: "kit 2026-10-23.1 does not read it, kit
    2026-10-28.1 and later do";
  - on Windows 11 with a newer kit: "Windows has no verdict on this installation yet, see its report";
  - with no kit version at all: both of the last two causes.

  `$script:FirstKitWithSupportReadings` and `Test-KitOlder` hold the boundary.
- *Symptom: TPM advice for every block.* On Windows 10, every block that was not the processor was
  told to "turn on TPM / Secure Boot in the firmware settings": SystemDriveSize, Memory, DX12,
  "none" (a block Windows does not name) and any unknown code. The rule-on reason had done that
  before; the new default note repeated it for every such laptop. On Windows 11 it was the reverse:
  a TPM-only block got only "updates are not guaranteed". It is W11-001 again, in the kit.
- *Fix.* The advice is given only when every code is Tpm, TpmVersion, UefiSecureBoot, SecureBoot or
  Uefi. Any other block points to "Can move to Windows 11" (Windows 10) or "Hardware supported by
  Windows 11" (Windows 11) in the report, which names each reason. A block with no named reason says
  so. A TPM-only block on Windows 11 adds "To make it supported, turn on TPM / Secure Boot in the
  firmware settings".
- *How to verify.* `kit-tests.ps1`, under "Windows 11 on unsupported hardware", each with the rule off
  and on:
  - Windows 11 zips from an old kit, a current kit and one with no kit version;
  - a blockers row alone, on Windows 10 and on Windows 11;
  - no report, on both builds;
  - SystemDriveSize, Memory, DX12, Tpm with SystemDriveSize, an unknown code, and none;
  - Tpm, TpmVersion, SecureBoot and Uefi alone, which keep their advice.

  Each new check of a fixed case fails on the kit before the fix. The checks of cases that were
  already right (Tpm alone, a processor block on Windows 11, Windows 10 with no report) pass before
  and after.
- *Replay.* The pilot zips and the not-fleet runs, copied under `%TEMP%`: the three pilot rows and
  the T470p's 2026-10-26 run do not change, with the rule off or on. The dev desktop's Windows 11 zip
  now says "kit 2026-10-23.1 does not read it", and with the rule on its CHECK AGAIN says the same.
- *Not changed.* The app's Windows 10 detail for a block that is not the processor still ends "TPM
  and Secure Boot can often be switched on in the firmware (BIOS/UEFI) settings", and the kit's note
  now points to it. Its list of reasons is right. That sentence is the app's, and was not changed
  here.

**Date.** 2026-10-27; review round 2026-10-28.

---

## OS-001 — An unenrolled Windows 10 company laptop is graded "supported until 2027-10-12"

**Status: fixed on 2026-10-27 (found the same day),** as Step A rows 8 and 9 of
`docs/REMEDIES-PLAN.md`. `os.supported` now decides from an ESU licence or the build, out of one
table of Microsoft's dates. Update age is counted from the release of the cumulative update the
running build came from. Pending updates are split by type. See "Fix" below.

**Symptom.** `os.supported` reads **Good**, "Windows 10 security updates (incl. consumer ESU) end
2027-10-12", on all three pilot laptops and on the dev T470p. On the dev T470p, all 24 ESU licence
rows in `SoftwareLicensingProduct` read `LicenseStatus` 0, which means none is licensed. The build is
19045.6466, the November 2025 out-of-band build for unenrolled devices, which carries the October 2025
security content. That laptop has had no Windows security fix since October 2025, and the report says
it is fine. "Days since last successful update" reads Good on it too.

**Root cause.**
- `SysOverviewModule.cs:17` hard-codes the consumer ESU end date, and `:77` grades "supported" as
  `DateTime.Now <` that date. Nothing reads whether ESU is active.
- Microsoft says consumer ESU "can't be used in commercial scenarios", so on a company laptop the
  date is wrong as well as unchecked.
- `UpdatesModule.cs:31-39` and `:83` take the newest hotfix install date and the newest
  successful update of any kind, Defender definitions included. After a reimage, every hotfix is
  dated the reimage day.

**The wrong first guess.** HW-001 (2026-10-21) moved the date from 2026-10-13 to 2027-10-12 because
consumer ESU had been extended, and its "Reality" column called the old Bad grade wrong. The old
Bad was nearer the truth for an unenrolled PC. The fix should have asked "is this PC enrolled, and
may it use consumer ESU?", not "what is the latest date?".
`HardwareAuditFixTests.Windows10_end_of_support_tip_names_the_extended_date` locks the 2027-10-12
text and has to change deliberately with the fix.

**How to verify.**
- Read-only, unelevated:
  - `Get-CimInstance SoftwareLicensingProduct -Filter "Name like '%ESU%'"`: the
    `LicenseStatus` of each row;
  - `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` `UBR`: the running build.
- Compare both with the `os.supported` row of a scan on the same PC.

**What the fix found on the way.**
- **"The newest security update" would have been wrong too.** In Windows Update's cache on the dev
  T470p, the newest installed update classified "Security Updates" is KB5126256 of September 2026.
  It is the *ESU Licensing Preparation Package*, not a security fix. Counting it would have called
  this PC patched in September 2026.
- **The update history cannot name the newest Windows update either.** The cumulative update
  KB5071959, installed and running, sits in the history with result code 1 (in progress). Most
  history entries throw when asked for their categories.
- **Pending updates were all offers.** The 13 waiting were 12 driver offers and 1 firmware update
  (`DriverClass` "Firmware"); none was a security update.
- **A latent licence bug.** `LicenseModule` took the first licence row with a key as "the Windows
  licence". Once an ESU key is installed, its add-on row has a key too, and could have been
  reported as the Windows licence, MAK channel included. No PC here has an ESU key, so it never
  showed.

**Fix (2026-10-27).**
- **One table, `Support/WindowsSupport.cs`.** It holds every end date with its Microsoft source
  URL, "checked on 2026-10-27", the three published commercial ESU activation IDs with their year
  ends, the consumer ESU end, and the build boundary. A test checks that every date is a Patch
  Tuesday. A Wednesday would mean someone took a lifecycle page's "6:59:59 AM next day" stamp
  literally.
- **"Today" is never earlier than the table's checked-on date,** so a clock set back cannot make
  an ended Windows look supported.
- **Windows 10 22H2 is Good only on evidence.** One way is an active licence (`LicenseStatus` 1)
  for the ESU year that covers today, matched by Microsoft's activation ID, never by name; Year 1
  alone no longer covers it. The other is a build above 19045.6466, the last build Microsoft offers
  a PC without ESU. Otherwise it is Bad: "No Windows security updates since October 2025...".
  - A key installed but not activated, and an earlier year's licence, are named in the detail.
  - On Home, the detail says company ESU covers Pro, Enterprise and Education only and BitLocker
    needs Pro (decision 10), and an info row `os.home_edition` says it on every Windows version.
  - A build-only Good never names an end date: consumer ESU and a company ESU without a key look
    the same from here.
- **LTSC and Windows 11 use their own rows by edition.** LTSC 2024 shares 24H2's build 26100. An
  edition or build not in the table (Server, SE, newer builds) is a Note, not graded.
- **`os.win10_eol_near` is removed; `os.support_ending` replaces it.** It is a Normal row when the
  support that applies (a version, an LTSC, or an ESU year) ends within about six months.
- **Windows 11 on unsupported hardware (decision 9).** `os.win11_hardware_supported` is Normal when
  Windows' own appraiser verdict for the next Windows 11 version is Red for a known requirement. With
  no verdict, it is a Note; the processor name is never guessed from.
- **Licence and registry reads that throw** go through `failures.Record`, and then no
  `os.supported` row is written.
- **Update age.** `os.patch.age_days` is now the days since the *release* of the cumulative update
  the running build came from. The KB comes from its servicing package's manifest; the release
  date (`LastDeploymentChangeTime`) comes from Windows Update's cache. Hotfix install dates and the
  history are no longer read for it. The bands are Good ≤ 42 and Normal ≤ 70: a five-week Patch
  Tuesday gap plus a week to install stays Good, and only two missed months are Bad.
- **Pending updates.** `updates.pending_count` counts security and quality updates only; Defender
  definitions and optional updates are named in the detail. Drivers and firmware get their own
  info rows. A zero is graded only when Windows Update searched in the last 7 days; otherwise it
  is a Note, "Windows Update has not searched since...". A non-zero count stays graded as a lower
  bound.
- **`LicenseModule`** skips `LicenseIsAddon` rows.
- **The test this fault named has changed on purpose.**
  `Windows10_tips_never_give_a_company_laptop_the_consumer_esu_date` replaces
  `Windows10_end_of_support_tip_names_the_extended_date`.

**Verified on the dev T470p (unelevated scan, 2026-10-27).**
- `os.supported`: Bad, "No Windows security updates since October 2025: no Extended Security
  Updates licence is active, and this build (19045.6466) has nothing newer."
- `os.patch.age_days`: 350, Bad (KB5071959, released 2025-11-11).
- `updates.pending_count`: 0, Good, from a search the same day; the 12 driver and 1 firmware
  offers are on their own rows.

**Not verified:** any Windows 11 machine. The `os.win11_hardware_supported` reading assumes Windows
11 keeps appraiser verdicts for its next version, as Windows 10 does. The RollupFix package naming
was seen on Windows 10 only. If either differs, the row or the update age becomes a Note, not a
false grade.

**Review round, 2026-10-28.** Six faults in the fix above, all found by reading the code. No PC
showed any of them.
- **A Year-1-only PC read Good for two more years (the OS-001 class again).**
  - *Symptom.* The PC has the Year 1 licence active and Year 1's builds (19045.7811). On 2026-10-28
    it read Good, "No company ESU licence is active here, so this is consumer ESU or a company ESU
    delivered another way". Both halves were false, and the verdict held until 2028-10-10.
  - *Root cause.* With the covering year (Year 2) not active, the code fell through to the build
    check, and Year 1's own builds passed it. The sentence for this case ("The Year 1 licence is
    active, but Year 1 ended...") ran only at build 6466, which no PC that used Year 1 has. The test
    for it used 6466, so the suite missed the bug.
  - *Second fault.* Build evidence never expired. A Pro PC on consumer ESU stays above 6466 after
    2027-10-12 and read Good until 2028-10-10.
  - *Wrong first guess.* That the update-age row would catch it. On 2026-10-28 the October update
    is 15 days old and reads Good, and that row's own Bad tip sends the reader to "OS support
    status".
  - *Fix.* If any published ESU year's licence is active, the licences alone decide, so Year 1
    without Year 2 is Bad with "Year 2 is needed now". The build counts only when no licence is
    active, and only until consumer ESU ends on 2027-10-12, with an `os.support_ending` warning
    from six months before. After that, a build with no licence is Bad.
- **Enterprise and Education were told they might have consumer ESU.** Microsoft's consumer ESU
  is for "Home, Professional, Pro Education, or Workstations" only. Their wording now names only a
  company ESU that needs no key on the PC, through Windows 365 or Azure Virtual Desktop
  ([cloud and virtual ESU](https://learn.microsoft.com/en-us/windows/whats-new/enable-extended-security-updates-virtual)).
  "Subscription activation" is gone, because no Microsoft ESU page names it.
- **"Activate Year 3 before then" on a PC whose Year 3 is already active.** Years activated in a
  row now extend the cover, and the warning names the last of them. Microsoft does not say whether
  a later year's key activates early. If it cannot, this case never happens and nothing changes.
- **Ended editions missing from the table read "NOT graded" instead of Bad.** Added, each from its
  lifecycle page:
  - Windows 10 Enterprise LTSB 2015 and IoT Enterprise LTSB 2015, ended 2025-10-14;
  - IoT Enterprise LTSB 2016, ended 2026-10-13;
  - IoT Enterprise LTSC 2019, supported to 2029-01-09;
  - Windows 10 Team (EditionID `PPIPro`, builds 19042 and 19045), ended 2025-10-14. Microsoft: it
    "doesn't have an extended support option", so it is never sent to the ESU check.
- **On a PC managed by WSUS or Configuration Manager, the update age read too young.**
  `LastDeploymentChangeTime` is "the last published date of the update ... on the server that
  deploys the update", so on such a PC it is the date the server approved the update. The app now
  reads the `UseWUServer` policy (only that switch, never the server's name). When it is 1, the age
  is a Note, not graded. Of several cached dates for one KB, the earliest now counts, because the
  latest could only make an update look newer.
- **A Windows 11 verdict carried in a disk image was trusted as this PC's.**
  - *Fix.* A verdict dated before `Win32_OperatingSystem.InstallDate` is now a Note, not Good or
    Normal, and its blocker codes are withheld.
  - *Unverified: whether Sysprep resets that date.* No Microsoft page says so. This PC is evidence
    that it does: it was clean-installed from Microsoft's own generalized image, and its install
    date (2026-10-19 00:27) is later than that install's first boots, so setup writes the date on
    the PC itself, in the same passes a generalized image runs.
  - *What it cannot catch.* An image that was not generalized keeps its old date, and its verdict
    is still trusted. GOLDEN-IMAGE-PLAN already forbids deploying such an image.
  - *Side effect.* After a feature update, the old verdict is a Note for about a day, until the
    appraiser runs again.
- **How to verify.** In `WindowsSupportTests`, `WindowsUpdatesTests` and `Win11ReadinessTests`,
  run each case both ways (fails, then passes). Put the old ESU logic back and 20 of the new or
  changed cases fail. Live on the dev T470p (unelevated, 2026-10-28) nothing moved:
  - `os.supported` is Bad with the October 2025 wording the kit matches;
  - the Windows 11 verdict, dated after the install, is graded;
  - the update age is graded, because there is no WSUS policy here.
- **Known limit.** A PC whose ESU comes through Windows 365 or Azure Virtual Desktop has no
  licence row this app reads. Microsoft names a registry flag for the Windows 365 case
  (`SoftwareProtectionPlatform\ESU` `Win10CommercialW365ESUEligible`). It is not read yet.
  - On **Enterprise and Education**, the build alone can only mean such an ESU, because consumer
    ESU is not offered for them. From 2027-10-13 to the end of Year 3 (2028-10-10), they get a
    Note, "whether security updates still arrive was NOT established", instead of Bad. This was
    changed after the gate's re-check, under rule 3; the test is
    `After_consumer_esu_a_build_alone_on_enterprise_is_not_judged_until_esu_ends_for_good`.
  - On **Pro**, consumer ESU is the likelier explanation, and it ends on 2027-10-12. So from
    2027-10-13 Pro reads Bad, and the detail says to check a keyless company ESU where it is
    managed.
  - **KMS-activated ESU rows** (one per year, IDs not published by Microsoft) are not matched. A PC
    whose ESU years are activated through KMS reads Bad once the build stops counting. Microsoft
    documents only MAK, so this is a guess, not an observed case.

**Review round, 2026-10-28: the guide's date for the ESU note.** `AUDIT-GUIDE.md` said the
"Windows 10 without ESU" note needs a zip "from a stick built on or after 27 October 2026". The
stick built that day carries kit 2026-10-26.1, whose app still grades every Windows 10 supported
until 2027-10-12, so its zips never carry the note, and a reviewer could read the missing note as
"ESU is fine". The guide now says kit 2026-10-28.1 or later, and that on an older zip a missing note
does not mean the laptop has ESU. See PRIV-002 for the fix and its check.

**Date.** 2026-10-27; review round 2026-10-28.

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

**Correction, 2026-10-27 (OS-001).** The `os.supported` row of the table above was itself wrong. The
2027-10-12 date applies only to a PC enrolled in consumer ESU, which Microsoft does not allow for
company use, and this T470p is not enrolled. So "supported until 2027-10-12" is the new misgrade.

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
