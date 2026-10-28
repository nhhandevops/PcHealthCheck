<!-- Written 2026-10-27 by a multi-agent review (a code survey, five brainstormers, a skeptic and a
     planner), then revised the same day after the owner's answers by four more agents, each checked
     by a skeptic. It is a plan, not a commitment. Phase 1 Step A (rows 1-13) was built and reviewed
     on 2026-10-27/28; everything after Step A is still a plan. The owner's decisions
     are in section 6; what they change, and what is still open, is in the first section below. -->

# Plan: from "what is wrong" to "what to do about it"

## Update, 27 October 2026: the owner's decisions, and what they change

The owner answered section 6's questions on 27 October 2026. Four agents worked out what the answers
change: reimaging, a freshly reimaged laptop, the Windows version and the language. A skeptic
re-checked each agent's code references and outside facts. Where this section and the older text
below disagree, this section wins.

### Two uses, not one

- **Before the wipe: the kit's intake check.** The reimage erases everything about the old Windows
  install: junk files, startup items, browser memory, updates waiting, event history. What still
  matters:
  - the hardware: drive, battery, keyboard, memory;
  - the BIOS settings;
  - the edition and licence;
  - which Windows the laptop can take;
  - whether its data is backed up.

  So D2-D9, M2-M4, S1-S3, S5, H5, H6 and O4 drop out of the kit's prep notes.
- **In daily use: the app, after handover.** Every remedy in section 3 still applies there, except
  in-app deletion (decision 3).

### Reimaging restores a disk image; it is not a fresh install

The reimage source is two Rescuezilla (Clonezilla-format) disk images on the audit drive, both made
in November 2025. One was captured on a ThinkPad T14 Gen 1. The other, the "under 230 GB" image, was
captured on the pilot T470p itself; its firmware details match.

- **The T470p image came from the SSD that is now retiring sectors.** Verify it in Rescuezilla
  before restoring it to another laptop.
- **The T14 Gen 1 image brings another model's drivers onto a T470s.** If it was not generalized
  with Sysprep, every laptop restored from it also shares one Windows identity. Both are unverified,
  see "Still open".
- **A restore brings the image's state with it:** its install date, computer name, Windows Update
  cache, event logs and Windows 11 verdict. So the kit must never:
  - decide from `WindowsInstalled` whether a laptop was reinstalled;
  - treat the restored event history as covering the intake check;
  - trust a cached Windows 11 verdict without its date.

  Instead, the technician declares the handover run (Step D below).
- **Each restored laptop starts about a year behind on updates.** "Install updates, then Restart"
  is a required handover step, not advice.
- **The audit drive now carries the images as well as the results.** It is the same drive that
  logged disk errors on the T470p. Keep a second copy of the images on a healthy drive.

### Windows 10 or 11: facts for decision 7

These come from Microsoft's lifecycle and ESU pages and Lenovo PSREF, checked on 2026-10-27. Check
them again before buying anything. ESU means Extended Security Updates.

- **Neither processor is supported by Windows 11:** the i7-7820HQ (T470p) and the i7-7600U
  (T470s). The i7-7820HQ's 2021 listing covered only select devices, such as the Surface Studio 2.
  Windows' own check says `CpuFms` on all three pilot laptops.
- **Windows 10 22H2 left support on 2025-10-14.**
- **Consumer ESU** runs to 2027-10-12, but Microsoft says it "can't be used in commercial
  scenarios".
- **Commercial ESU** covers Pro, Enterprise and Education. The price is USD 61 per device for Year
  One, doubling each year, cumulative, with three years at most. Year One ended on 2026-10-13.
  Enrolling now therefore means paying Years One and Two together: about USD 183 per device to
  2027-10-12, an inference from the list prices that the reseller should confirm. Reimaging needs
  the prerequisite updates and the ESU key re-applied, and it can use up MAK activations.
- **Windows 10 Home**, which the pilot T470p runs on a retail licence, is very probably outside
  commercial ESU (Microsoft sends Home customers to consumer ESU; not fully verified). Home has no
  BitLocker either. Any company path for that laptop starts with a Pro licence.
- **Windows 10 Enterprise LTSC 2021** supports both processors and ends 2027-01-12. It has had its
  own paid ESU on sale since 2026-09-01. It needs volume licensing, and Microsoft says it is not meant
  for most PCs.
- **Windows 11 on unsupported hardware:** Microsoft says it is not recommended, gets no support, and
  updates are not guaranteed.
- **Windows 11 24H2 Home and Pro also left support on 2026-10-13**, so "Windows 11" alone does not
  mean supported.
- **The T470p's `Tpm` blocker:** Lenovo lists a discrete TPM 2.0 chip for the T470p and the T470s,
  but Windows sees no TPM on the pilot T470p. It is probably switched off in the BIOS. That is an
  inference, not a reading.
- **Policy:** `requireWindows11Ready` is on in `audit-rules.json`, so every 7th-generation laptop
  stays REPLACE, however clean the reimage. Whether it stays on is the owner's call.

### A fault the review found: the app calls an unenrolled Windows 10 "supported"

This is OS-001 in `docs/warning_bug_and_solutions.md`. It was fixed on 2026-10-27/28 (Step A rows 8-9,
and the review round's follow-ups).
- `os.supported` reads Good, "updates end 2027-10-12", on all three pilot laptops and on the dev
  T470p. That date is the consumer ESU end, and the app never checks for an ESU licence.
- On the dev T470p, all 24 ESU licence rows are unlicensed and the build is 19045.6466. It has had no
  Windows security fix since October 2025.
- "Days since last update" reads Good as well. It counts Defender definitions and hotfix install
  dates, and a reimage sets those dates to the reimage day.

The fix is Step A rows 8 and 9. They ship as one change: grading updates more strictly without the
ESU reading would leave nothing that says the laptop gets no security updates.

### A freshly reimaged laptop must read clean

Today, a clean Windows 10 22H2 with only the four apps would get an overall Bad from false alarms:
- Windows Security's tray icon counted as unsigned (S2, already in Step A);
- "Quick scan never run" graded Bad on a new install; on the dev PC the first scheduled scan came 4
  days after install;
- pending updates graded by count, although on the dev T470p all 13 waiting were driver offers;
- the time service "stopped" graded, although W32Time starts on demand on workgroup PCs;
- encryption rows for the kit's own USB stick.

Also, the startup check prints "every item has a valid publisher signature" even when some items
could not be verified or carry a broken signature (`StartupModule.cs:83-84`). Whether "nothing is
backing up your files" fires too depends on the elevated scan and on restore points; the baseline
below measures it.

These become Step A rows 10-13. A restored image carries the image's own scan, reliability and
update history, so the baseline decides which of them matter on the company's images.

### The startup allowlist (decision 2)

The startup check today:
- reads only Run keys and Startup folders;
- records no signer;
- ignores Task Manager's "disabled" for Startup-folder items;
- never reads services or scheduled tasks, which is where NetBird's service and Chrome's updater
  live.

**The matching rule.** An item counts as allowlisted only when its file name **and** a signer that
verified offline match the same entry. Display names and Run value names never count. A file named
like an allowed one but signed by someone else is reported as exactly that. An item whose
signature could not be checked is never allowlisted.

| App | File | Signer | How it starts |
|---|---|---|---|
| Chrome | `chrome.exe`, Google Updater | Google LLC | Google Updater services and tasks; no Run entry expected (unverified) |
| Unikey | `UniKeyNT.exe` | PHAM KIM LONG (4.3 and later; older builds are unsigned) | HKCU Run `UniKey`, read from the binary, not yet seen in practice |
| LightShot | `Lightshot.exe` (a starter) | Kilonova LLC (its updater: OOO Lightshot) | HKLM 32-bit Run `Lightshot`, seen on the dev PC |
| NetBird | `netbird-ui.exe`, `netbird.exe` | NetBird GmbH | HKCU Run `netbird` (0.75 and later), service `Netbird` |

- **The signers are provisional.** Each came from a binary on the dev PC or from vendor material.
  Confirm each one from the binaries on a restored test laptop before shipping.
- **The list is data, not code.** Signers change: LightShot has used several names, and NetBird's
  certificate expires 2027-04-18. A mismatch reads "unknown item", never Bad.
- **Two more classes are never flagged:**
  - Windows' own items: SecurityHealth, Edge's auto-launch and updater, and OneDrive if present;
  - maker drivers: Lenovo, Synaptics, Intel, Realtek and ELAN signers.
- **NetBird still gets an information line**, "VPN agent: NetBird". A VPN joined to someone else's
  network is exactly what a handover should catch.

### The handover check (Step D, rewritten)

- **Two runs per laptop.** The technician runs the kit before the wipe (the intake) and again after
  it (the handover). The handover run is started from a separate `3 - Handover check.cmd`, so the
  kit knows which run it is from what the technician chose, not from install dates.
- **One row per laptop.** The handover run joins the laptop's row by serial and hardware UUID, not by
  computer name (`audit-common.ps1:379-410`). A laptop with no firmware serial would split into two
  rows, so the handover run always asks for the serial.
- **Fix the merge before the first handover run:**
  - **Remarks.** Remarks from earlier checks are dropped today. Carry each one, labelled with its
    check time, until a later finished check answers the same question.
  - **Findings.** A later scan replaces earlier findings wholesale. Supersede reading by reading
    instead:
    - a processor-based REPLACE stays;
    - drive and memory errors logged before the restore are never cleared by the image's restored
      logs.
  - **Battery.** Ask the battery-swelling question again; never inherit the intake's "ok".
  - **Updates.** "0 updates waiting" counts only from a search made during the handover session.
- **A Handover column, beside the verdict:**
  - Not run;
  - PASSED;
  - NOT PASSED: the items that failed;
  - NOT ESTABLISHED: the items that could not be read.

  The verdict itself does not change.
- **Handover items:**
  - updates searched in this session and installed, then Restart;
  - no devices without drivers;
  - the four apps present and signed;
  - startup items only from the Windows, maker and allowlist classes;
  - Windows activated;
  - time zone and clock sync;
  - no `Windows.old`;
  - no extra local accounts;
  - on Windows 10, the ESU state;
  - encryption, if the owner sets that policy.
- **Unikey and typing.** In Telex mode Unikey rewrites typing ("dd" becomes "đ"), which could
  corrupt a serial or a note typed into the kit. Test it in the VM. The kit may need to say "switch
  Unikey to English (Ctrl+Shift) before typing".

### Profile sizes (decision 4)

- **What the intake records.** For each Windows account, the signed-in one included: for example
  "account 2 (not signed in): 35 GB, last used 400 days ago".
- **Accounts are numbered, never named,** under principle 10. The technician sees the names on the
  laptop itself.
- **The kit also asks** "Is this laptop's data backed up (an image or a copy)? y/n".
- **What we know so far:**
  - every elevated pilot run found exactly one account;
  - one timing sample walked 26 GB in about 30 s.
- **A fix on the way.** `AllUsersStartupModule` already writes other accounts' names into the report
  (`AllUsersStartupModule.cs:100,114,121`), against principle 10. Fix it in the same step.

### Language (decision 6)

- **English stays the language of the data.** That covers the JSON, receipts, console output, the
  history, `kit.json`, the CSV and the fault log. Vietnamese is only how text is shown. The kit
  reads the app's English text, so translating the data would bring KIT-005 back.
- **The app window and HTML report** get a new Step E, after Step B.
  - The translations are per-language JSON embedded in `PcHealth.Core`. There are no satellite
    assemblies (set `WithCulture="false"`), and the files are kept out of `Rules/`.
  - Rule tips are overlaid by rule id.
  - The size: about 200 interface strings, 100 metric labels, 213 tips and 25 forecasts.
  - Module details stay English at first; there are about 900 of those strings.
  - The language follows the Windows display language, with English as the default and a toggle.
- **The kit's questions** print the English line and then the Vietnamese line. The Vietnamese comes
  from a UTF-8 strings file, so the `.ps1` files stay ASCII.
  - Consolas has every Vietnamese letter; Lucida Console has only a few. If a laptop's console font
    lacks them, the English line still reads.
  - Test on the VM and a test laptop before shipping.
- **Menu paths.** About 46 tips name English menu paths, and several are wrong even on an English
  Windows 10: Activation, Troubleshoot, "Privacy & security" and "Startup apps". Fix them in Step A.
  Phase 2's buttons (`ms-settings:` links) remove the need for paths.

### Testing (decision 5)

- **Virtual machine first, then spare test laptops.**
- **The host.** A Windows 11 VM needs a host whose processor Windows 11 supports, and Hyper-V needs
  Pro. The dev desktop qualifies; the T470 laptops do not.
- **The fresh-image baseline:**
  1. a Windows 10 22H2 VM;
  2. then each real reimage image restored onto a spare laptop;
  3. each scanned before updates, after updates, and after the Lenovo drivers.

  Expected: nothing but real prep items.
- **Privacy.** Those scans carry UUIDs, serials and names, so they are never committed unscrubbed.

### What decision 3 removes

- **Removed:**
  - the deletion receipt type (Phase 2 row 2);
  - the in-app Temp clean-up and Recycle Bin (Phase 3 row 1), with its test set;
  - the mass-delete risk.
- **The scope:** the app never deletes the user's or Windows' files. It still removes its own
  temporary files, and its history when asked.

### Still open, for the owner

*Answered the same evening: see section 6, decisions 8-15. Only the ESU purchase is still open. The
list below is kept for the record.*

1. **Which Windows** goes on a reimaged laptop (decision 7)? Does the company buy commercial ESU?
   Should "Windows 11 does not support this processor" still mean REPLACE?
2. **The pilot T470p** runs Home on a retail licence, and its SSD is failing. Buy Pro for it, or
   retire it?
3. **The images:** which image goes on which model? Which Windows edition does each hold? Were they
   generalized with Sysprep?
4. **Encryption:** must handed-over laptops have drive encryption? BitLocker needs Pro.
5. **Backup:** do handed-over laptops need a file backup: OneDrive, File History, or none? This
   decides whether "nothing is backing up your files" is a prep step or stays silent.
6. **Language:** which Windows display language does the image use?

Until each is answered, the app reports the facts, the kit's verdict rules stay as they are, and
nothing that depends on the answer is built.

## 1. Summary

Today the app tells you what is wrong, but its tips are fixed sentences. They do not use the machine's own numbers, and they never connect two readings. This plan adds a recommendation layer that works in three tiers: **explain, point, then maybe act**.

- **Explain.** For each real problem, the app says what it found and what the item is for. It also says what each option costs and roughly how much it would help, using numbers measured on that machine.
- **Point.** A button opens Windows' own tool, such as Storage Sense, Disk Cleanup or Startup apps. Then "Measure again" shows the real before and after.
- **Act.** The app acts itself only where Windows has nothing equivalent. It always shows a preview first, keeps an undo, and writes a counted receipt.

The pilot decides the order of work:

- On the three pilot laptops, the one real disk problem was hardware: one laptop's SSD was retiring damaged sectors.
- RAM was fine: 16 GB on each, with no low-memory warnings.
- C: had 52-82% free. The only "disk nearly full" warning came from the auditor's own USB drive.

So the first job is to remove false alarms and to put hardware and backups first. Cleanup, memory and start-up advice come next, and they stay silent on a healthy PC. The audit kit gets prep steps for the technician, and it never acts.

## 2. Principles every remedy follows

1. **Explain, then point, then maybe act.** Anything Windows already does well, the app opens; it does not copy it. The app's own value is the diagnosis, the explanation and a measured before and after.
2. **A real trigger on the right thing.** Advice appears only when a reading is graded Normal or Bad, on an internal drive or on this user's data.
   - A healthy PC gets no recommendations.
   - A failed size query shows "not measured" or "at least X GB". It never shows 0 or "nothing to clean".
   - A check skipped on purpose is a Note, not a failure. Examples: the scan was not elevated, the browser was closed, the log is too short.
3. **Hardware and data first.** On a failing internal drive the first advice is "back up now". The app offers no cleanup, benchmark, defrag or disk repair on that drive.
4. **Consent and preview.** The app asks every time. The preview and the action use the same list, so what you see is exactly what happens. The preview shows:
   - the category, count, size and date range, next to today's date;
   - what is lost;
   - "can be undone" or "permanent".

   Nothing the user owns is ticked by default.
5. **Undo.** Before any change, the old value goes to a changes file, with a one-click revert and a 15-day history. The app writes only through Windows' own settings, using exactly the bytes Windows writes.
6. **Receipt.** Every action is counted, not promised. Example: "Startup: LightShot switched off for this account (was on); undo available." Paths are written as `%TEMP%`, never `C:\Users\<user>`. Each Windows tool the app opens is a receipt line.
7. **Never user files.** The app never touches:
   - Documents, Desktop, Downloads, Pictures, OneDrive or redirected folders;
   - Outlook `.pst` files;
   - other users' profiles;
   - browser logins and site data;
   - event logs, crash reports, restore points;
   - the page file, the hibernation file or Windows' own stores.
8. **Offline.** No remedy makes a network connection without saying so first. Folders that live on a server are skipped, with a Note.
9. **Admin.** Company laptop users are usually standard users, so every remedy works without admin. Admin is used only for read-only measurements. The headless scan and the kit never get an action.
10. **Privacy.** Reports, the JSON and the history carry only:
    - totals, counts and program names.

    They never carry:
    - file or folder names, user names, SIDs, volume GUIDs or serials;
    - account e-mails, tab titles, URLs or command lines.

    Each new kind of reading is added to PRIVACY.md, the report line and the receipt in the same commit.
11. **Check the clock before using file dates.** A wrong clock makes every file look old. The app shows today's date and the files' date range, and it refuses when the clock looks wrong.
12. **The right account.** Per-user readings (Temp, Recycle Bin, browsers, startup) run only in the normal, unelevated app. They describe the signed-in account, and the text says so.
13. **Honest estimates.** Every number says how it was obtained (measured, at least, or typical) and how sure it is. The wording is "up to" and "may help", never "will fix". Freed GB is space, not speed.
14. **No false alarms.**
    - No thresholds on counts of things, such as startup items or processes.
    - New bands start as information only.
    - The three pilot reports are replayed after every rule change, and the results compared.
    - At most 5 recommendations, then "N more", plus "don't show again".

## 3. The remedies

Each remedy lists: what the user sees, what the app checks, the advice, the optional action and its undo, the estimate shown, and how success is measured.

### Disk space

**D1. USB drives stop counting as the laptop's free space** (a fix, not a remedy)
- **User sees:** "E: is a USB drive. Its free space is shown for information and does not count toward this PC's health. 451 GB free of 931 GB."
- **App checks:**
  - It maps each volume to its partition, then to its disk, and reads the disk's connection type. This works without admin.
  - USB volumes become information only. The drive Windows runs from is always graded, whatever its connection.
  - If the connection cannot be read, the grade stays as it is today, with a Note.
  - The same test applies to drive readings that came from a USB disk. On 2 of the 3 pilot reports, uncorrected read errors on the auditor's drive were graded Bad.
- **Advice:** Internal drives keep today's tips. For a USB drive there is an information-only tip: empty its Recycle Bin, or move old backups off it.
- **Action and undo:** None. This is an app change only.
- **Estimate shown:** None. One false alarm disappears.
- **Success:** In the pilot replay, the Storage card goes from amber to green on all three laptops, and no internal-drive grade changes.

**D2. "What is using space on C:"**
- **User sees:** One section per low drive:
  - **Windows can clean this:** temp files older than 7 days, and this drive's Recycle Bin.
  - **A setting, not junk:** the hibernation file (D4).
  - **Your decision:** Downloads, and how much of it is installers (D8).
  - **Needed by Windows, not offered:** the page file, reserved storage and the installer caches (D5).
  - **App caches:** shown last, only above about 2 GB, labelled "refills" (D9).

  The section also says:
  - whether Storage Sense is on;
  - whether an old Windows copy (Windows.old) exists, and its date;
  - for example, "C: ran out of space 3 times in 90 days", taken from Windows' event log.
- **App checks:**
  - It walks folders only when an internal drive is already Normal (under 20% free) or Bad (under 10%). On a healthy PC no folder is walked and no file name is read.
  - It has its own consent row.
  - It reads names, sizes and dates only. It never opens a file, so OneDrive files are never downloaded.
  - It never follows links. Cloud-only files count as 0 bytes.
  - Each category has a time limit. A category cut short says "at least X GB".
  - Folders it could not open are counted, and the result says "at least".
  - Folders that live on a network server are skipped, with a Note.
  - The Recycle Bin is sized per drive with Windows' own call, not the Shell total. On the probe PC, 47.6 of the 48.8 GB the Shell reported belonged to a drive that was not plugged in.
  - File names never leave memory.
- **Advice, safest first:**
  1. Turn on Storage Sense.
  2. Use Disk Cleanup.
  3. Review the Recycle Bin.
  4. Uninstall unused apps.
  5. Move large media off C:.

  If the drive is failing, the first item becomes "back up and replace", with the line "freeing space does not fix a failing drive".
- **Action and undo:** The Windows-page buttons of Phase 2 (D3). The app deletes nothing here.
- **Estimate shown:** Per category, for example: "Temporary files: about 3.1 GB that no program has touched for 7 days (7.5 GB in total; the rest is in use or recent)." A folder the app could not read says "needs administrator rights", never 0.
- **Success:** "Measure again" shows the drive and each category before and after. The history shows whether Temp fills up again. A Temp folder that keeps growing points to a program leaving files behind.

**D3. Temp files and the Recycle Bin**
- **Why this is mostly Windows' job:**
  - Disk Cleanup's "Temporary files" category already uses a 7-day rule, and Storage Sense repeats it on a schedule.
  - On the probe PC, Storage Sense was off, so 7.55 GB stayed in Temp even though Windows' silent cleanup had run that day.
- **User sees:**
  - "Turn on Storage Sense": it keeps cleaning, which a one-off clean cannot do. If a company policy controls it, the text says "IT controls this setting".
  - "Open Disk Cleanup for C:".
  - "Open Recycle Bin".
  - "Measure again".
- **App checks:** Storage Sense on or off, and whether a policy controls it. It reads the sizes from D2.
- **Advice:** Storage Sense first, Disk Cleanup second.
- **Action and undo (Phase 2):** The buttons only. Each launch is a receipt line, and nothing on the PC changes until the person uses Windows' tool.
- **Action (Phase 3, only if you still want it after field use):** An in-app clean-up.
  - **What it offers:**
    - It offers a top-level item in Temp only if every file inside it was created and written more than 7 days ago. That is 3.09 GB on the probe PC.
    - A simple per-file rule would take 6.49 GB, and 3.41 GB of that is in folders used this week, one of them a running tool's.
    - Folders unpacked from a zip or rar are listed separately and left unticked, labelled "may hold your edits".
  - **What the screen shows:**
    - The 20 largest items, with untick boxes, then "and 5,618 smaller items, 0.6 GB".
    - The names are on screen only and never saved.
  - **When it refuses:** If any file is dated in the future, or the clock was recently corrected by more than an hour without a sync since.
  - **How it deletes:**
    - File by file, without admin, never through a link.
    - Files in use are skipped, and it removes only the folders it emptied.
  - **Recycle Bin:** For one internal drive, through Windows' own "permanently delete N items" prompt.
  - **Undo:** None for Temp. The dialog says "permanent" plainly.
- **Estimate shown:** "About 3.1 GB." Then the counted figure: "Freed 2.9 GB of an estimated 3.1 GB; 41 items in use were left."
- **Success:** The counted receipt, the drive's free space re-read, and the history showing whether Temp refilled.

**D4. The hibernation file (advice only)**
- **User sees** (only when the system drive is Bad): "Hibernation file: 6.3 GB. When the laptop hibernates, Windows saves memory here, so open work survives a flat battery. Fast Startup uses part of it. It is not junk."
- **App checks:**
  - The size, from a listing of C:\. Asking for the file directly fails without admin and would look like "no file".
  - The state, from the registry. It never parses the `powercfg` text, which is translated into each Windows language.
- **Advice:**
  - Laptops: keep it.
  - Desktops: an admin step, `powercfg /h /type reduced`, frees about half and keeps Fast Startup.
- **Action and undo:** None. It trades battery safety for disk space, and it needs admin.
- **Estimate shown:** The exact size. "Reduced" is about half, marked "approximate".
- **Success:** The next scan reads the new size.

**D5. "Needed by Windows, not offered" (advice only)**
- **User sees** (only when C: is low): one block with:
  - the page file: 8.8 GB; programs here needed up to 1.8 GB of it;
  - reserved storage: about 6.5 GB, so updates still install on a full disk;
  - Windows Installer and Package Cache: what programs need to repair or uninstall themselves.

  Each line says why it stays.
- **App checks:** The sizes, the page-file settings, and the reserved-storage registry key.
- **Advice:** Leave these alone. Moving the page file to another drive is suggested only when all of these hold:
  - a second internal drive exists;
  - memory use is Good;
  - there were no low-memory warnings;
  - the log covers at least 7 days.
- **Action and undo:** None. Removing any of these would break the app's own checks: memory, page file, crash dumps and update failures.
- **Estimate shown:** The sizes only.
- **Success:** A test makes sure these are never recommended for removal.

**D6. An old Windows copy after an upgrade (advice plus a button)**
- **User sees:** "Previous Windows installation: at least 18 GB, made on <date>. It lets you go back to your previous Windows version. Windows removes it on its own in N days. Removing it now frees the space, but you can no longer go back."
- **App checks:**
  - Without admin: whether the folder exists, its date, and a size marked "at least".
  - With admin, read-only: the full size and the rollback window.
  - The day count uses the clock check.
- **Advice:** If the upgrade is new and might need rolling back, wait. Otherwise use Disk Cleanup, then "Clean up system files".
- **Action and undo:** The "Open Disk Cleanup" button. The app never deletes this folder itself.
- **Estimate shown:** "Up to X GB", because some files are shared with the new Windows.
- **Success:** The folder is gone and the free space is re-read.

**D7. System leftovers: update files, crash dumps, error reports (advice plus a button)**
- **User sees:** "Open Disk Cleanup, then press 'Clean up system files'. Windows shows its own size for each category." There are two warnings:
  - Keep crash dumps and error reports while this PC has recent crashes or graphics resets. They are the evidence for a repair.
  - If Windows' image check says "repairable", run the repair first. A clean-up is not a repair.
- **App checks:** The existing crash and image-health readings.
- **Advice:** Leave restore points alone.
- **Action and undo:** The "Open Disk Cleanup" button only.
- **Estimate shown:** Windows' own figures, in Windows' dialog.
- **Success:** The free space is re-read. The backup and event readings must not change.

**D8. Downloads and OneDrive (advice plus a button)**
- **User sees:**
  - "Downloads: 1.4 GB, of which installers 1.2 GB (11 files). Deleting an installer does not uninstall the program."
  - OneDrive: "Free up space keeps your files in OneDrive and downloads each one again when you open it, which needs the internet."
- **App checks:** The total size, the installer count and the installer size. Names and sizes only.
- **Advice:** Sort Downloads by size in Explorer. Never turn on Storage Sense's automatic Downloads delete on company laptops: it deletes the user's files by age alone.
- **Action and undo:** "Open Downloads". The app deletes nothing.
- **Estimate shown:** The exact sizes. The gain depends on the user's choice.
- **Success:** Downloads is re-measured.

**D9. Browser, Teams, Office and developer caches (advice only)**
- **User sees:** One total, only above about 2 GB, ranked last: "Brave's cache: 1.7 GB. Copies of pages so sites open faster. It refills within days."
- **App checks:**
  - Cache folders only. It never sizes or lists logins, cookies or site data.
  - Outlook `.pst` files are your mail, never "cache". Only `.ost` files are a re-downloadable copy.
- **Advice:** Each app's own control:
  - browser: "Clear browsing data", then "Cached images and files" only;
  - Teams: Reset;
  - Office: delete cached files only after the Upload Center is empty;
  - Outlook: the "download mail for the past N months" slider;
  - developer tools: their own clear commands.
- **Action and undo:** None. A running browser keeps its cache open, and the Office cache can hold edits that are not uploaded yet.
- **Estimate shown:** The size, with "refills".
- **Success:** The next scan. If the cache is back within a week, the text says so.

### Memory and browsers

**M1. Honest history span** (a fix that also covers disk errors and crashes)
- **User sees:**
  - "Windows' history on this PC starts on 2026-10-19, so this count covers 8 days, not 30. Zero means no warnings in those 8 days."
  - In the kit: "Memory history: 1 day (laptop reinstalled), not judged."
- **App checks:**
  - The oldest record in the log, the window and the Windows install date.
  - A non-zero count stays graded.
  - A zero from less than about 7 days of history becomes ungraded, with a Note and no "could not finish" banner.
  - If the clock looks wrong, the span reads "unknown".
  - The same check applies to every "last N days" count: disk errors, hardware errors, crashes, unexpected shutdowns.
- **Advice:** Scan again after two or three weeks of normal use.
- **Action and undo:** None.
- **Estimate shown:** The exact span.
- **Success:** A machine with an old log gives exactly the same report as today. The probe PC shows "8 days".

**M2. Short of memory, or just cached?** (this gates all other memory advice)
- **User sees:**
  - "Cached memory is Windows keeping recent files ready. It is handed back instantly. 'RAM cleaner' tools that empty it make the PC slower."
  - Then one of: "No sign that memory ran short since Windows last restarted", "short at times", or "short now".
  - Example: "Since Windows last restarted 4 days ago, programs were promised up to 33.7 GB, more than the 27.8 GB it normally hands out, so Windows had to grow the page file."
- **App checks:**
  - **Now:** memory use and available memory.
  - **Since the last restart:** the peak against the limit, and the page-file peak. These count only after 4 hours of uptime.
  - **History:** the low-memory warnings, with the span from M1.
  - New bands are information only until they are checked on both dev PCs and the three pilot reports.
- **Advice:**
  1. If memory is not short, say so plainly.
  2. If it is, close or restart the program M3 names.
  3. If the page file is set to a small fixed size, let Windows manage it (text only; needs admin and a restart).
  4. Browser settings (M4).
  5. Add RAM (M6).

  Never use RAM cleaners. Never turn off the page file, SysMain or memory compression.
- **Action and undo:** None. There is deliberately no "free RAM" button.
- **Estimate shown:** None. This is a diagnosis.
- **Success:** Fewer low-memory warnings over the following weeks, in the history. A lower reading right after closing tabs proves nothing.

**M3. Name the culprit from Windows' own records**
- **User sees:** "Each time Windows nearly ran out of memory, it wrote down which programs held the most. Brave was there in 2 of the 4 moments, with up to 5.5 GB in one process. dwm.exe, the part of Windows that draws the screen, held 2.5 GB. That is not normal."
- **App checks:**
  - The low-memory event (2004) already counted today. It now also reads each event's list of top programs: name and size only.
  - It uses the same source filter as today, because Windows Setup writes an event with the same number.
  - The figure is per process, not per program, and the text says so.
  - Windows' leak reports are a lead only, never a trigger.
  - This also works in the kit on a freshly started laptop.
- **Advice, by kind:**
  - browser: M4;
  - dwm.exe: M5;
  - a virtual machine or local AI model: it needs more memory than this PC has, so give it less or add RAM;
  - a developer tool: restart or update it;
  - a one-off: nothing.

  This replaces today's "open Event Viewer" tip.
- **Action and undo:** None.
- **Estimate shown:** The exact size at each warning.
- **Success:** The program stops appearing in new warnings.
- **Privacy:** Add "names of programs Windows recorded at each low-memory warning" to PRIVACY.md.

**M4. The browser** (only when M2 or M3 show a shortage)
- **User sees:** "Brave holds 8.2 GB in 46 processes, with 1 window open. Browsers run each site in its own process, so many processes are normal." Then the options, each with what is lost:
  - restart the browser (tabs reopen);
  - Memory Saver or Sleeping tabs (paused tabs reload when clicked, and text typed into one can be lost);
  - stop the browser running in the background after it is closed;
  - remove unused extensions (the report already lists them).
- **App checks:**
  - For each browser: total memory, process count, and whether a window is open.
  - Browser policy keys, only so it can say "set by your organization".
  - It follows the evidence. The probe PC's latest warning named powershell and dwm above the browser.
  - Firefox reads "not measured".
- **App does not read:** command lines, the browser's settings files, tab titles or history.
- **Advice:** As above. For the fleet, IT can switch these settings on centrally.
- **Action and undo:** The settings path is shown as text. A button appears only when that browser is already running, and its dialog says the browser may go online. The app never ends processes and never writes browser settings.
- **Estimate shown:** "Up to 8.2 GB if closed." For settings: "the next scans will show the effect".
- **Success:** The browser's total at the next scan, and the low-memory warnings over the following weeks.

**M5. dwm.exe or a driver leak (advice only)**
- **User sees:** "dwm.exe normally holds a few hundred MB. Twice it held over 2.5 GB when Windows ran short. That is often a graphics driver fault. Signing out and back in frees it now; save your work first."
- **App checks:** It fires only when dwm held over 1 GB in at least 2 low-memory records. The driver's date never fires it alone.
- **Advice:** Update the display driver from the laptop maker. That needs the internet and admin. In the kit: "update the display driver before handover".
- **Action and undo:** None. The Device Manager button says it may contact Windows Update.
- **Estimate shown:** "About 2.5 GB each time it builds up."
- **Success:** dwm stops appearing in new records.

**M6. When the honest answer is "add RAM" or "a lighter role" (advice only)**
- **User sees:** "This laptop has 8 GB in one of its two slots. Windows ran short 5 times in the 30 days it remembers. A second 8 GB DDR4-2400 SODIMM doubles the memory and makes it run in dual channel." Or: "All slots are full. This laptop suits a lighter role."
- **App checks:** It speaks only when all three hold:
  - 8 GB of RAM or less;
  - evidence of a shortage;
  - no leak (M5) explains it.

  The slot details come from the kit's existing hardware readings and are worded "firmware reports". An unknown module type reads "unknown", never "soldered".
- **Advice:** Add a matching module, replace both modules, or reassign the laptop.
- **Action and undo:** None. This is hardware.
- **Estimate shown:** The exact installed type and speed.
- **Success:** The next audit shows the new RAM. The warnings fall over the following weeks.

### Slow start-up

**S1. "Shut down" is not a restart (advice only)**
- **User sees:** "This PC has been switched on 2 times since Windows last really restarted, 4 days ago. Here, Shut down saves Windows' core to disk so power-on is faster. That is normal. Updates that need a restart wait for Restart. Choose Restart, not Shut down, once."
- **App checks:**
  - It reads whether Fast Startup is on, and counts the fast starts since the last full start, in log order.
  - It fires only when uptime is over 7 days AND (a restart is pending, OR memory is under pressure).
  - It is ranked below hardware findings.
- **Advice:** Restart about once a week. Keep Fast Startup on.
- **Action and undo:** None. There is no "Restart now" button (it would close the person's work) and no Fast Startup change.
- **Estimate shown:** None. It names what a restart finishes: "3 waiting updates".
- **Success:** At the next scan: uptime under a day, no pending restart, and the last start is a full start.

**S2. Startup programs: what each one costs**
- **First, a fix:**
  - The signature check misses Windows files that are signed through a catalog. All three pilot laptops show "1 unsigned startup item", and it is Windows Security's own tray icon.
  - Fix it by falling back to catalog verification. It stays offline.
- **User sees:** One row per item:
  - name and publisher;
  - kind (Windows, security/VPN/management, hardware helper, sync/backup, chat, updater, unknown);
  - memory it holds now with no window open, with helper web views credited to their parent program;
  - what changes if it stops starting with Windows.

  Example: "Google Drive starts with Windows and holds 0.2 GB with no window open. If it does not start, files sync only while it is open."
- **App checks:**
  - The existing startup list.
  - The startup count moves from Security to Performance and stays information only.
  - The count itself is never graded. Items are listed only above a memory floor checked on healthy PCs.
- **Advice:**
  1. The program's own "start with Windows" setting.
  2. Settings, Apps, Startup.
  3. Uninstall what nobody uses.
- **Action and undo:**
  - **Phase 2:** "Open Startup apps" or Task Manager's Startup tab.
  - **Later in Phase 2:** an in-app switch for this user's items only.
    - It writes exactly what Windows writes, captured on Windows 10 and 11.
    - The old bytes go to the changes file, with one-click undo and a receipt line.
    - The app re-reads the value afterwards.
  - **Never offered:**
    - Windows' own items;
    - sync and backup clients (the backup check would still say "backing up");
    - security, VPN and management agents;
    - items set by policy.
- **Estimate shown:**
  - Memory: "up to X MB", which is zero if you open the program every morning anyway.
  - Start-up seconds: "not measured", unless Windows itself named the item (S5).
- **Success:** The value reads "Disabled". After the next sign-in the program is not running. Memory is compared at a similar uptime.

**S3. Starts the startup list misses (advice only)**
- **User sees:** Scheduled tasks that start at sign-in or boot, automatic services, and Store-app start-up tasks. For each: name, publisher, how it starts, and memory held (services). Remote-access and VPN agents carry the line: "on a laptop changing hands, confirm the company installed it."
- **App checks:** Information only, in Performance, worded "tasks this account can see". SIDs and user names are stripped. The publisher comes from the signature, not the folder.
- **Advice:** Leave Microsoft items alone. Do not follow "disable SysMain / Search / telemetry" guides. Uninstall software nobody needs.
- **Action and undo:** None. Disabling tasks or services breaks updaters and security agents, and it needs admin.
- **Estimate shown:** Memory for services. Tasks read "not measured".
- **Success:** After an uninstall, the next scan no longer lists its tasks and services.

**S4. When the real cause is hardware** (a rule in the engine)
- **User sees:** "This laptop runs Windows from a hard disk. That alone usually explains a slow start; turning off startup apps will help only a little." Or: "The drive Windows runs from is failing. Back it up first."
- **App checks:**
  - If the system drive is a hard disk or is failing, or RAM is low with warnings, startup advice is ranked below the hardware advice and labelled "will not fix the slow start".
  - It uses internal drives only. A drive that was not read never counts as an SSD.
  - A hard disk used only for data does not trigger it.
- **Advice:** The drive and memory remedies (H1, H4, M6).
- **Action and undo:** None.
- **Estimate shown:** None, on purpose.
- **Success:** Tests in both directions. On a healthy SSD, startup advice still appears.

**S5. Start-up time measured by Windows** (Phase 3, admin, read-only)
- **User sees:** "Your last 7 starts took a median of 74 s from power-on to a usable desktop. These are Windows' own timings; the time spent at the password screen is not counted. Windows said <program> slowed 2 of them."
- **App checks:**
  - Windows' boot-timing log, which needs admin.
  - The median over starts of the same kind; full starts and fast starts are never mixed.
  - Program file names only, no paths.
  - Without admin it adds a Note. If Windows recorded no timings, it adds a Note, never "fast".
  - It is graded only against the machine's own history.
- **Advice:** The same as S2, plus driver and BIOS updates through IT.
- **Action and undo:** None.
- **Estimate shown:** "Up to N s", from Windows' own figure.
- **Success:** The median over the next 3 full starts, compared with the 3 before. The text says updates in between can also explain a difference.

### Drive health

**H1. The laptop's own drive is failing or worn (advice only)** — the fault the pilot actually found
- **User sees:** One card per drive: "The drive Windows runs from (internal SATA SSD, holds C:) is retiring damaged areas: 5 so far. Drives that start doing this fail far more often, some without further warning." Then:
  - **What to do first:** copy your files off. About 104 GB is in use, roughly 20 minutes to a USB 3 drive.
  - **What is already protected:** the exact folders, for example "OneDrive backs up Desktop, Documents and Pictures; nothing else on this drive is backed up".
  - **What is lost if nothing is done:** possibly everything on the drive.
- **App checks:**
  - The existing drive-health readings, joined with the volumes on that drive, the backup readings and encryption.
  - It triggers only on damaged-media counters. Cable errors and temperature never trigger it.
  - It never speaks from a drive that was not read.
  - Before this ships, the File History and OneDrive readings must move out of the admin helper. Today they describe the admin's profile when a technician types an admin password.
- **Advice:**
  1. Copy files off now, and check the copy opens.
  2. Replace the drive with a known-brand SSD:
     - size: used space times 1.5, rounded up;
     - interface: the same as the old drive;
     - check the laptop manual for the physical size.
  3. Do not run defrag, a full disk repair, a benchmark or "repair" tools on it first.
  4. For IT, on disposal:
     - if BitLocker was on, delete the key and wipe;
     - if not, destroy the drive or use the maker's secure erase.
  5. A drive graded Good is no guarantee: many drives fail without warning.
- **Urgency:**
  - **Act now:** the drive predicts its own failure, a counter is at its limit, or there are pending or uncorrectable sectors.
  - **Check again in 1-2 weeks:** a small, steady count with no history.
  - **A dated forecast:** only with 14 days or more of history.
- **Action and undo:** "Open backup settings" and "Open File History". These change nothing.
- **Estimate shown:** The used GB (exact), the copy time ("about"), and the urgency tier.
- **Success:** After replacement the next scan shows a new drive with zero counters, and the kit row goes from REPLACE to READY.

**H2. Never stress a failing drive** (a safety fix)
- **User sees:** "Disk speed was not measured on C:: this scan's own drive check found 5 retired sectors. Measuring writes 1 GB to the drive, and a drive in that state should be backed up and left alone."
- **App checks:**
  - Today the benchmark trusts only Windows' own health flag, and that flag said "Healthy" on the pilot's failing SSD.
  - The fix passes the app's own findings to the benchmark. A drive is skipped if:
    - its damaged-media counters are Normal or Bad;
    - they grew in the history; or
    - the event log shows media errors on it.
  - *As built (BENCH-001, 2026-10-28):* any event-log disk error that names the drive skips it. Disk
    errors that name no drive (controller resets, file-system errors) and are counted against the
    PC skip every internal drive, with a Note.
  - Drives are matched by device id, never by model name.
  - Cable errors and temperature do not block. Unknown health still measures, with a Note.
  - One shared check guards every future heavy disk task.
- **Advice:** Avoid heavy use of a failing drive until your data is copied off.
- **Action and undo:** Enforced in code. The receipt shows no file written to that drive.
- **Estimate shown:** None.
- **Success:** Tests include two identical drives, only one of them failing.

**H3. USB disk errors read as the PC's disk failing** (a fix)
- **User sees:** "16 disk errors in 90 days: all 16 on a USB drive; your internal SSD logged none. This usually means unplugging without ejecting, a loose cable, or a weak port."
- **App checks:**
  - It reuses the kit's existing split (internal against external), so the PC stops being graded on USB errors. An unknown connection counts as internal.
  - Surprise-removal events from USB drives become information only. On an internal drive they still count.
  - Readings taken through a USB adapter say "reported through a USB adapter".
  - **Phase 3:** the detailed log that tells "drive disconnected" from "damaged media". It reads named fields only and never the serial.
- **Advice:** Eject before unplugging. Use a port on the PC or a powered hub. If the drive holds the only copy of anything, copy it off.
- **Action and undo:** None.
- **Estimate shown:** Exact counts per drive.
- **Success:** In the pilot replay, laptop A's internal disk errors read 0. On the probe PC, the Storage card no longer reads Bad.

**H4. Windows runs from a hard disk (advice only)**
- **User sees:** "Windows runs from a hard disk. Starting Windows means many small scattered reads. A hard disk does about a hundred a second; an SSD does thousands. That is usually why an older laptop feels slow."
- **App checks:**
  - A new information-only reading, in Performance.
  - The connection is checked first, so a USB box that claims "SSD" is not trusted.
  - A RAID setup with no other clue reads "not determined".
  - Windows' own disk score is used only when it is valid, with its date.
- **Advice:**
  1. Replace it with an SSD of used space times 1.5.
  2. Until then, keep SysMain on, keep the weekly optimisation on, and keep 15% free.
  3. If RAM is low too, the SSD comes first.
  4. If a failure counter is up, H1's "back up first" wins.
- **Action and undo:** None.
- **Estimate shown:** Speed ranges by drive type, worded "many times faster". No boot-time promise.
- **Success:** The next scan reads SSD. With S5, the boot median before and after.

**H5. An internal SSD that is nearly full (advice only)**
- **User sees:** "C: is on an internal SSD and has 8% free. Windows' own record shows it dropped to 3% this week. SSDs need spare room to write quickly. This app measures read speed only, so it will not claim a write speed-up."
- **App checks:**
  - Windows' daily "lowest free space" record, shown only below 10%.
  - The volume id is turned into a drive letter in memory and never reported.
- **Advice:** Free space to 15-20% through D2 and D3.
- **Action and undo:** None. Windows already tells the SSD which space is free, both on delete and weekly.
- **Estimate shown:** The GB needed to reach 15%, exact.
- **Success:** The lowest-free figure over the following week.

**H6. Windows' drive optimisation turned off (advice plus a button)**
- **User sees:** "Windows' weekly drive optimisation is turned off on this PC. It tells SSDs which space is free and tidies up hard disks. Turning it back on changes one Windows setting and can be undone."
- **App checks:**
  - It flags only a disabled task, or no run in 30 days or more on an internal drive.
  - The TRIM row appears only when an internal SSD exists.
  - Windows' own monthly SSD "defragmentation" is normal and is not reported.
- **Advice:** Turn the schedule back on in Optimize Drives. Remove the "optimiser" tool that turned it off. Never defragment an SSD by hand.
- **Action and undo:** "Open Optimize Drives". The person changes the setting in Windows' own window.
- **Estimate shown:** The last-run date and the fragmentation as Windows measured it.
- **Success:** The task reads Ready, and the last-run date moves within a week.

**H7. Healthy drives graded amber** (a fix)
- **User sees:** "Drive temperature 40 °C: normal for an SSD."
- **App checks:**
  - Temperature bands by drive type. Today an SSD at 40 °C is graded Normal on hard-disk bands with "mechanical drive" text; pilot laptop A shows it. Tests: 75 °C is Normal and 85 °C is Bad.
  - The Detail text is written by drive type.
  - There is no TRIM row on PCs without an SSD.
  - Later: SATA wear "0%" becomes "not reported", but only after one admin check and your OK, because some fleet rows would move to "Not read".
- **Advice / action / estimate:** None. These are grading fixes.
- **Success:** In the pilot replay, only the expected rows change.

### Other

**O1. Windows 11 blocked by a switch that can be turned on (advice for the technician)**
- **User sees:**
  - "TPM is off: turn on Intel PTT / AMD fTPM in the BIOS. Windows checks again within about a day; then run the kit again."
  - Or: "Processor not supported: cannot be fixed; plan a replacement."
- **App checks:** The existing Windows 11 blockers, and the date Windows last checked. If that date is older than the change, the text says "check again".
- **Advice:**
  1. Suspend BitLocker and have the recovery key before any TPM or BIOS change.
  2. On an old BIOS-style install, switching on Secure Boot stops Windows starting. Convert with `mbr2gpt` after a backup, or reimage.
- **Action and undo:** None. The app never touches firmware and never starts Windows' check.
- **Estimate shown:** "Probably ready after the change", if this was the only blocker.
- **Success:** The next kit run after Windows re-checks.

**O2. The previous employee's profile is still on the laptop (advice only)**
- **User sees:** "2 other user profiles, last used about 140 and 400 days ago. Reimage, or remove them after the data-retention check."
- **App checks:** The count and "last used" ages, with the clock check. No names, SIDs or paths.
- **Advice:** Reimage first. Otherwise use Windows' User Profiles dialog, after HR or IT agree.
- **Action and undo:** None. Deleting a profile cannot be undone.
- **Estimate shown:** No sizes, unless you say yes to question 4.
- **Success:** The next run shows 1 profile, or a newer install date.

**O3. The laptop runs hot and slows itself (advice only)**
- **User sees:** "The processor slowed itself 14 times this month because it was too hot or not getting enough power. Usually that is dust in the fan or old heat paste. Also check the charger's wattage."
- **App checks:** It needs two signals together: a high temperature AND (a low clock under load OR Windows' own throttle events). In the kit the text says "clock under load not measured".
- **Advice:**
  1. Keep the vents clear.
  2. A technician cleans the fan and replaces the paste.
  3. Use the full-wattage charger.
  4. Install the maker's BIOS updates.
- **Action and undo:** None. There are no power-plan changes.
- **Estimate shown:** None.
- **Success:** The CPU benchmark against the machine's history, and the throttle count over 30 days.

**O4. Security settings switched off (advice plus a button)**
- **User sees:** "Windows Firewall is off for Public networks." Then "Open Windows Security". On a managed laptop: "This may be set by your IT team; ask them first."
- **App checks:** The existing security readings, plus whether the laptop is company-managed (domain, MDM, policy keys).
- **Action and undo:** The button only. The app never toggles security settings and never removes Defender exclusions.
- **Success:** The next scan grades it Good.

**O5. Is the clock right?** (a new reading that the other remedies depend on)
- **Why:** Today's time check reads only whether the time service runs. On the probe PC the service was not started, and Windows logged one 14-hour correction.
- **App checks:**
  - large manual or system corrections with no sync since;
  - files dated in the future;
  - the last successful sync.

  Small clock moves after sleep are normal and are ignored.
- **Used by:** the Temp clean-up dates, profile ages, the Windows.old day count, and the kit.

## 4. Roadmap

### Phase 1: advice only. Nothing on the PC changes.

**Step A: fix what today's reports get wrong.** These ship in today's fleet output, and they cost less than any remedy.

| # | Item | Effort |
|---|---|---|
| 1 | USB volumes stop counting as free space (D1) | small |
| 2 | USB disk errors split from internal ones in the app (H3, without the detailed log) | small |
| 3 | Catalog-signed Windows files stop reading as unsigned (S2) | medium |
| 4 | SSD temperature bands; TRIM row only with an SSD (H7) | small |
| 5 | Benchmark skips drives the app's own checks call failing (H2) | medium |
| 6 | Honest history span for every "last N days" count (M1) | small |
| 7 | Backup readings move out of the admin helper (H1 prerequisite) | small |
| 8 | Windows 10 support status (OS-001). Read the ESU licence for the year that covers today: `SoftwareLicensingProduct` add-on rows, picked by `LicenseIsAddon`, not by name. Also read the running build. The end dates live in one data table with sources and a "checked on" date. A failed read goes through `failures.Record`, never a Note. Ships with row 9 | medium |
| 9 | Updates. Pending updates are split: security and quality updates graded, drivers as information, firmware on their own line. A zero from a stale or never-run search is not graded. The newest Windows update is identified by its KB number or the build, never by its install date or its translated title | small |
| 10 | Young-install Notes. Defender's "quick scan never run" and an empty reliability history become Notes on a young install. Windows 11 "not assessed" keeps the kit's CHECK AGAIN, with clearer words | small |
| 11 | Time service "stopped" becomes information. USB volumes' encryption rows become information (the D1 rule) | small |
| 12 | Startup check. Record the signer. Never count "could not verify" as verified. Apply Task Manager's "disabled" to Startup-folder items. Write paths as `%VAR%` tokens. Number other accounts, never name them | medium |
| 13 | Tips whose menu paths are wrong on Windows 10 get version-neutral wording | small |

Rows 8-13 come from the update at the top. A pilot replay cannot prove rows 8 and 9, because the
kit's verdict does not read those metrics and all three pilot reports are already Bad overall. Prove
them on a fully patched Windows 10 22H2, with and without ESU. The dev T470p is the "without" case
today.

**Step B: the recommendation engine, advice only.** Effort: **medium**.
- What it adds: a "What you can do" section in HTML, the GUI and JSON. It has:
  - numbers and drive names in the tips;
  - hardware blockers first;
  - a cap of 5 plus "N more", and "don't show again".
- What it carries: S1, S4, H1, O1, O3, O4 and the advice text of D4-D9, M5 and M6.
- How it is tested: a healthy example gives zero recommendations, and a failed module gives "not measured". Then the three pilot reports are replayed.

**Step C: new read-only measurements**, each with Notes, all information only at first.

| # | Item | Effort |
|---|---|---|
| 1 | Low-memory culprits from Windows' records (M3) | small |
| 2 | Fast Startup state and starts since the last restart (S1) | small |
| 3 | System drive type: hard disk or SSD (H4) | small |
| 4 | Clock health (O5) and profile count and age (O2) | small |
| 5 | Browser memory by name, plus policy keys (M4) | small |
| 6 | Old Windows copy, lowest free this week, optimisation state (D6, H5, H6) | small |
| 7 | Memory shortage against cache readings (M2) | medium |
| 8 | Startup cost per item, tasks and services (S2, S3) | medium |
| 9 | "What is using space" breakdown, run only when C: is low (D2) | medium |

**Step D: the kit, intake and handover** (rewritten 27 October, see "The handover check" at the top).
Effort: **medium to large**. It has three parts:
1. **The intake prep steps:** hardware, BIOS, edition and licence, which Windows the laptop can take,
   and data to back up, with the profile sizes of decision 4.
2. **The handover check:** `3 - Handover check.cmd`, the Handover column, and new `audit-rules.json`
   keys, off by default.
3. **The merge fixes it needs first.**

The gates, each tested in both directions:
- a fresh Windows 10 VM passes with no false items;
- a VM with one item missing or unreadable does not pass;
- the pilot replay, plus synthetic runs after a reimage.

**Step E: Vietnamese in the app window and report** (decision 6, see "Language" at the top). Effort:
**medium**. Most of the time goes on translating and reviewing about 27,000 characters of tips, and
the owner reviews the safety tips (back up first, swollen battery, failing drive). The kit's
bilingual questions ship with Step D.

**Before moving to Phase 2:**
- The pilot replay changes only the rows we expect.
- There are no recommendations on healthy examples, and none on the pilot laptops apart from the hardware ones.
- Both dev PCs have been scanned, one of them elevated, to set the new bands.
- PRIVACY.md, the README, the main-window header and the report line are updated for each new reading, in the same commit.
- The probes have been repeated on one Windows 11 machine. Every probe so far ran on Windows 10 22H2.
- All tests, kit tests and the daily smoke are green.

### Phase 2: guided actions, with preview and undo

| # | Item | Effort |
|---|---|---|
| 1 | Fix the confirm dialog's "with administrator rights" label, which it shows even for actions that need no admin | small |
| 2 | New receipt type: setting change (old value to new value). The deletion type is dropped by decision 3 | small |
| 3 | "Open the right Windows page" buttons: Storage Sense, Disk Cleanup, Recycle Bin, Downloads, Startup apps, Optimize Drives, Backup, Windows Security, Device Manager. Check each target on Windows 10 and 11 | small |
| 4 | "Measure again", plus before and after in the history (free GB, category sizes, memory peak, warning count, startup count) | medium |
| 5 | Startup switch for this user's items, with the changes file and one-click undo. First capture what Task Manager writes, in a Windows 10 and a Windows 11 VM | medium |

**Before moving to Phase 3:**
- Each action has:
  - a counted receipt and a re-measure;
  - a before and after scan on a VM, in which nothing else the app measures changed;
  - an ACCEPTANCE row, for example "Task Manager shows Disabled".
- The actions have been used for some weeks: in a VM first, then on spare test laptops (decision 5).

### Phase 3: larger work, only if Phase 2 shows the need

| # | Item | Effort |
|---|---|---|
| 1 | ~~In-app Temp clean-up and Recycle Bin through Windows' prompt (D3)~~ Dropped by decision 3: the app opens Storage Sense, Disk Cleanup and the Recycle Bin instead | — |
| 2 | Start-up time from Windows' own records, admin, read-only (S5) | medium |
| 3 | "Disconnected" against "damaged media" for USB disk errors (H3) | medium |
| 4 | SATA wear "not reported" (H7), after one admin check and your OK | small |
| 5 | Optional admin reads: full old-Windows size; Windows' startup-impact data. Profile sizes moved to Step D, since decision 4 is yes | medium |

The test set below was written for the Temp clean-up that decision 3 dropped. It is kept for the
record, and for any future action that walks files:
- a link to a canary file outside the folder;
- locked, read-only, hidden, system and future-dated files;
- a long path, a Unicode name and a folder it cannot open;
- a clock moved 60 days ahead;
- a standard user, and elevation with a second account's password;
- Defender switched on.

It runs in a VM or a throwaway account first, then on the T470p, and never first on a fleet laptop.

## 5. How it fits the company laptop audit

*Updated 27 October.* Laptops are reimaged, so the examples below about startup items, clean-up and
profiles apply only to laptops in daily use. For the kit they become intake steps (hardware, BIOS,
licence, backup) and handover items (see "The handover check" at the top). The TPM example
assumed a laptop that could take Windows 11; on a 7th-generation ThinkPad the TPM matters only for
BitLocker.

- **The kit stays assessment-only.** It runs unattended, leaves nothing on the laptop, and never gets an action. It stays ASCII and Windows PowerShell 5.1, with kit tests in both directions.
- **Verdicts do not move by themselves.** READY / FIX FIRST / CHECK AGAIN / REPLACE change only if you and the manager agree a new key in `audit-rules.json`. Every new key starts off.
- **The fleet table gets a "Prep steps for IT" column**, written for the technician. Examples:
  - "Updates waiting: 37 - install them, then Restart (not Shut down) before handover." Every pilot laptop had 12-37 waiting.
  - "Starts with Windows, not part of Windows or the company list: <names> - remove what the new employee does not need, or reimage." This appears only for items outside Windows, the maker and your allowlist. A laptop whose only item is Windows Security's icon gets no note.
  - "Remote-access/VPN agent starts with Windows: <name> - confirm the company installed it."
  - "Low-memory warnings: 4 in 30 days, top user dwm.exe 2.5 GB - update the display driver before handover."
  - "Memory history: 1 day (laptop reinstalled), not judged."
  - "Add memory: 8 GB installed (1 of 2 slots used); add one 8 GB DDR4-2400 SODIMM." This is today's FIX FIRST, with more detail.
  - "TPM is off - suspend BitLocker, keep the recovery key, turn on TPM in the BIOS, run the kit again tomorrow."
  - "C: 9% free. Old Windows copy at least 18 GB (upgrade 4 days ago), Temp 3 GB - run Disk Cleanup with system files."
  - "2 other user profiles, last used about 140 and 400 days ago - reimage, or remove after the data-retention check."
- **The right account:** Notes built from per-user data say "for the signed-in account", which may be the technician's account.
- **Fixes the fleet gets straight away:**
  - The false "1 unsigned startup item" disappears from every laptop.
  - Laptop A's SSD temperature reads Good.
  - "Last 30 days" counts state their real span.
- **For you, the operator, now:**
  - The auditor's USB drive reported 2 uncorrected read errors on two of the three pilot laptops (the third did not report the reading).
  - Check it with its maker's tool, connected directly to a PC.
  - If it holds the only copy of the Results, copy them off first.
  - Later, the fleet summary prints one line when the same USB drive reports errors on every laptop.

## 6. Decisions (27 October 2026)

The owner answered the open questions below on 27 October 2026:

1. **Reimaged: yes.** Every laptop is wiped and Windows is reinstalled clean before handover.
2. **The standard image is four apps, on every laptop:** Google Chrome, Unikey, LightShot and Netbird. These four are also the startup allowlist.
3. **The app never deletes the user's or Windows' files.** It still removes its own temporary files, and its history when asked.
4. **Profile sizes: yes.** Before a reimage, the kit records each Windows account's total size and when it was last used, so IT knows what to back up. Only one size per account is recorded, never file names. Accounts are numbered ("account 2"), never named, under principle 10.
5. **Testing:** first in a virtual machine, then on spare test laptops.
6. **Language:** English first, then Vietnamese. That covers the app window, its report and the kit's questions, where each English line is followed by its Vietnamese one. The fleet summary stays English. The owner did not pick it.
7. **Which Windows goes on a reimaged laptop: not decided.** The options, with dates and costs, are at the top under "Windows 10 or 11". All three pilot laptops fail Windows 11's processor check.

**Second round, the same evening (27 October 2026):**

8. **Windows: both.** Some laptops get Windows 10 and some get Windows 11.
9. **"Windows 11 does not support this processor" no longer means REPLACE** ("make it work in Win 11
   as well"). `requireWindows11Ready` goes off. The app and the kit must work on Windows 11
   laptops, including Windows 11 restored onto hardware Microsoft does not support, and they say
   truthfully what that means for updates.
10. **The T470p's Home licence:** notify it in the health check run, rather than decide it now. The
    report and the kit say what Home rules out for a company laptop: commercial ESU and BitLocker.
11. **The golden image is still being built.** The tasks to help are in
    [GOLDEN-IMAGE-PLAN.md](GOLDEN-IMAGE-PLAN.md): what to do with the two old images, the build
    steps, the owner's decisions A-J, and the app and kit tasks I1-I8.
12. **Drive encryption: not required.** BitLocker is optional, on request, with a guide.
13. **File backup on handed-over laptops: not required.** The kit stays silent about it. The app
    keeps its own grade for people who use it on their own PCs.
14. **The image's display language: English.** Vietnamese is downloaded later where wanted. The app
    follows the display language, so it shows English first.
15. **Go for Phase 1**, given on 2026-10-27. Step A is first.

Still not answered: whether the Windows 10 laptops will be enrolled in commercial ESU. The app
reports each laptop's ESU state either way.

The original questions, kept for the record:

1. **Are laptops reimaged before handover?** If yes, notes about disk space, startup and profiles are not needed; updates and hardware still are. *Default: keep those notes off until you answer.*
2. **Which company agents belong on the startup allowlist** (VPN, antivirus, device management, HR sync)? *Default: the agents seen on the pilot laptops, once you confirm them.*
3. **Do you still want the app to delete files itself**, once "open Disk Cleanup / Storage Sense, then Measure again" exists? *Default: no. Decide after a month of using Phase 2.*
4. **Should the kit read the size of other users' profiles?** It is the most privacy-sensitive read in this plan, and it is slow. *Default: no. Counts and "last used" ages only.*
5. **Where should actions be tested:** a Hyper-V VM, or Windows Sandbox? Either one means turning on a Windows feature on some PC. *Default: a VM on a spare machine, then the T470p.*
6. **Which language do company users read?** Menu paths written in English do not match a Vietnamese Windows. The kit is ASCII-only, so Vietnamese text there would lose its accents. *Default: English text, with buttons that open the page directly so no menu path is needed.*

## 7. Risks, and what was left out on purpose

**Risks and how the plan handles them**

- **Privacy.** The space breakdown reads file names into memory. On the probe PC, Temp held pieces of personal files. So it walks folders only when C: is already low, and names never leave memory.
- **Too much advice.** Advice appears only on graded problems. The list is capped at 5, "don't show again" exists, and kit notes appear only for unusual items.
- **The wrong account.** Per-user readings run unelevated, and the kit notes name the account.
- **A wrong clock.** The clock check (O5), the date ranges, and refusal on future dates.
- **Claiming a fix too early.** The wording is "may help". The proof is the history over weeks, not one number.
- **Windows 11 is not verified.** Every probe ran on one Windows 10 PC. The probes are repeated on Windows 11 before Phase 2.
- **Security software reacting.** This was the risk of a fast mass delete. Decision 3 removed it.
- **A restored image carries old state.** Its install date, logs, update cache and Windows 11
  verdict belong to the image. A fresh restore is never allowed to clear an earlier finding it cannot
  re-check.
- **Allowlist spoofing and signer drift.** A renamed file cannot pass, because the match needs a
  verified signer. Signers change, so the list is data, and a mismatch reads "unknown item", never
  Bad.
- **Windows support dates move.** Consumer ESU has been moved once already. The dates live in one
  data file with sources and a "checked on" date, and they are re-checked before each release.
  Consumer ESU is never treated as a company laptop's end date.
- **Hidden network access.** On domain laptops, some folders live on a server. They are skipped, with a Note.
- **Fleet rows moving when rules change.** Every rule change is checked with a pilot replay, and changes that move rows wait for your OK.
- **Effort.** Phase 1 is most of the work, and it alone fixes everything the pilot actually showed.

**Left out on purpose**

| Left out | Why |
|---|---|
| A "free up RAM" button or RAM cleaner | It empties the cache Windows hands back anyway, and makes the PC slower |
| Ending programs or browser processes | Unsaved work and uploads are lost |
| Reading browser command lines, settings files, tab titles or history | They hold the user's identity and browsing; the advice is the same without them |
| Writing browser settings or policies | The running browser rewrites them; on company laptops IT owns policy; adware works this way |
| A button that starts a closed browser | It restores all tabs, uses more memory, and goes online |
| Turning hibernation off, or changing Fast Startup | It trades battery safety for space, needs admin, and changes what "uptime" means |
| A "Restart now" button | It closes the person's work; Windows already offers it |
| Shrinking the page file, or removing reserved storage, Windows Installer or Package Cache | It breaks the app's own checks, updates, and program repair |
| The app deleting the old Windows copy | Windows has a supported remover; doing it by hand means taking over system files |
| Deleting browser, Teams, Office or Outlook caches | They refill; the Office cache can hold edits not yet uploaded |
| Moving Downloads to the Recycle Bin | It frees nothing until the bin is emptied, and risks the only copy of an installer |
| Component-store analysis, driver-package and Delivery Optimization sizes | Little to gain (0.01-0.12 GB on the probe PC); the analysis takes minutes and writes logs |
| Admin-wide startup switches, and disabling tasks or services | They need admin, belong to IT, and break updaters and security agents |
| SysMain, retrim, defrag, TRIM and schedule actions | Windows already does these; the case is rare; it would mean a new kind of admin action |
| Deleting shadow copies, clearing logs, disk repair `/f` or `/r`, wiping free space, firmware or BIOS changes, secure erase | They destroy backups or evidence, or risk the data; deleting shadow copies looks like ransomware |
| Deleting profiles | It cannot be undone; it is a data-retention decision |
| Any deletion of the user's or Windows' files by the app | Decision 3, 2026-10-27. The app opens Windows' own tools instead |
| A Vietnamese kit window that depends on the console font | The kit prints each English line and then its Vietnamese line, so a laptop whose font lacks the letters still reads |
| Any action in the kit or the headless scan | They run unattended and promise to leave nothing behind |
| Thresholds on counts (startup items, processes) | Healthy machines would trip them |
| Grading Windows' "start took longer than usual" events by their presence | They appear on ordinary machines |
| Driver age as a trigger | A maker's last driver is often old and still correct |