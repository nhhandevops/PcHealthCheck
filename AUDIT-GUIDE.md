# Laptop audit guide

How to check every old laptop with PC Health Check, and turn the results into one table that says
which laptops can go to a new employee. About 6 minutes per laptop: 3 for the scan, 3 for the
physical checks.

> PC Health Check is a release candidate. It has been tested properly on only a few PCs, so check
> the first 2-3 laptops, send their result zips back for review, and only then do the rest.

## What you need

- A USB stick with about 200 MB free. Any format works.
- The **local administrator password** for the laptops. Without it, drive health (the most
  important check on an old laptop) cannot be read.
- Each laptop's **charger**, because laptops are checked on mains power.

## 1. Prepare the stick (once)

1. Download `PcHealthCheck-<version>-win-x64.zip`.
2. Right-click the zip, choose **Properties**, tick **Unblock** if it is there, then click OK.
   This stops Windows from warning about every file inside it on every laptop.
3. Extract the zip onto the USB stick, into a folder such as `E:\PcHealthCheck`.
4. Check that the folder contains `1 - Check this laptop.cmd`, `2 - Summarize all laptops.cmd`
   and `PcHealthCheck.exe`. The kit needs the whole folder, not just one file.

## 2. Check one laptop

**Before you start:**
- Plug in the charger.
- Unplug every other USB drive. An external drive's health would be mixed into the laptop's.
- Sign in with any account and close open programs.
- If the laptop has just started, wait about 2 minutes.

**Then:**

1. Plug in the stick, open its `PcHealthCheck` folder, and double-click
   **`1 - Check this laptop.cmd`**.
2. If Windows says **"Windows protected your PC"**, click **More info**, then **Run anyway**. The
   app is not code-signed yet, and that is the only reason for this warning.
3. When Windows asks for **administrator permission**, click **Yes** or type the admin password.
4. Wait about 3 minutes while the check runs. The window shows its progress. When it says
   **"Scan saved"**, the scan is already on the stick.
5. Answer the questions about the laptop's physical condition. Type `y` if something is fine,
   `n` if there is a problem (then describe it in one line), or `s` to skip a check you cannot do
   right now. To answer them, have a mouse ready for the USB ports and a Wi-Fi network you can join.
   Each answer is saved to the stick as soon as you give it.
6. Wait until the window says **"Saved"** and shows the **Verdict**, then close it and unplug the
   stick. If the window is closed earlier, everything saved up to that moment is kept. The table
   says what was missing, and asks for the laptop to be checked again if the scan had not finished.

The result is saved on the stick as `Results\<serial>_<computer>_<date>_<time>.zip`, one zip per
laptop per run. The serial is the laptop's own, read from its firmware, so every file name is
unique and traceable. If a laptop reports no serial, you are asked to type the one on its label.
If you skip that, the file starts with `NO-SERIAL`. A window closed before "Saved" can also leave a
folder beside the zip, `Results\.work-<the same name>\`. Leave it there: it holds what that run
saved, and the summary reads it.

**Use one stick for the whole audit, and keep everything in `Results\` on it until the audit is
done**, even after copying it somewhere. Each check notes which results were already on the stick,
and that, not the laptop's clock, is how the table knows which of a laptop's checks came last. A
laptop checked on two sticks, or after its earlier result was taken off the stick, keeps every
finding of both checks, because nothing then says which came last.

**Nothing is left on the laptop.** All work happens on the stick, nothing is installed, no
benchmark runs, and no history is kept.

> **If the answer to the battery question is "no" (the case bulges):** unplug the charger and do
> not charge that laptop again. A swollen battery can catch fire.

## 3. Send the first results back

After the first 2-3 laptops, send the whole `Results\` folder: the zips, and any `.work-...` folder
a closed window left. They are used to find what the app got wrong before you check the rest. Each
zip, and each such folder, contains:

- The app's reports. These include the computer name, drive models and free space, the names and
  paths of startup programs, browser extension names, and the programs using the most memory. With
  kit 2026-10-28.1 or later, startup paths are written as `%LOCALAPPDATA%\...` and other accounts
  are numbered, so neither carries a Windows user name. Reports from older kits include it. Go by
  the kit version, not by when the stick was made: the stick made on 27 October 2026 carries kit
  2026-10-26.1. The kit shows its version when it starts ("Kit 2026-10-28.1"), and each zip
  records it in `kit.json` as `KitVersion`.
- The laptop's serial number, hardware UUID (a fixed ID its firmware reports, used to tell apart
  laptops that have no serial), model, BIOS version, memory, disks and Windows version.
- Your answers to the questions, and the app's console output.
- The kit's own log, `audit-transcript.txt`, from every kit. Windows PowerShell starts it with the
  computer name and the Windows user name the kit ran under.
- Short fingerprints of the names of the results already on the stick, not the names themselves,
  so the table can put the checks in order.

It contains no passwords, no product keys and no file contents. Check that your company allows
sharing this before you send it outside the company.

**Reviewing them on another computer:**
1. Pull the PcHealthCheck repository there.
2. Copy everything in the stick's `PcHealthCheck\Results\`, the `.work-...` folders included, into
   the repository's `audit-results\` folder. Git never uploads anything from that folder except
   its README.
3. Ask Claude to "review the audit results in audit-results/".

## 4. Make the table for your manager

When the laptops are done, double-click **`2 - Summarize all laptops.cmd`** on your own PC. It
creates three things in `Results\`:

- `fleet-summary.html`: one row per laptop, the worst first, each with its reasons and a link to
  that laptop's full report.
- `fleet-summary.csv`: the same table, for Excel.
- `fleet-reports\`: every laptop's full report.

A laptop checked more than once appears once, built from all its checks: its latest finished
check, plus anything another check found that no later check re-examined. A problem one check
found ("the battery bulges", a failing drive) stays on the row, with the check it came from, until
a later check that got to its end finds it fixed. So a closed window or a skipped question never
hides a finding, and a laptop repaired and checked again to the end is shown as it is now. If a
laptop's system board was replaced, it can appear as two rows with the same serial, each saying so:
the row checked later is the laptop as it is now.

### The verdicts

| Verdict | Meaning |
|---|---|
| **READY** | Nothing found that stops a new employee using it. It does not mean Windows 11 supports the laptop: the **Windows 11** column says that. |
| **FIX FIRST** | Usable after the repair or upgrade the reasons name: battery, drive, memory, cleaning. |
| **REPLACE** | It cannot meet the rules at any repair cost: a processor Windows 11 does not support. This happens only when `requireWindows11Ready` is turned on. It is off by default, so with the default rules no laptop is REPLACE. |
| **CHECK AGAIN** | Something important was not checked, for example the admin prompt was declined, so no verdict would be honest. |

**Notes about Windows** are information for IT. They never change the verdict:
- **Windows 11.** The note says whether Windows 11 supports the processor and what the options are.
  For a TPM or Secure Boot block, it says what to switch on. For any other block, such as a system
  drive under 64 GB, it points to the laptop's report, which names each reason. When there is no
  Windows 11 reading, the note says why, as far as the zip shows it: Windows has not assessed the
  laptop, Windows gave a verdict the app does not interpret, or the zip comes from a kit older than
  2026-10-28.1, which does not read Windows 11 support on a laptop already running Windows 11.
- **Windows 10 Home.** Microsoft's company Extended Security Updates (ESU) and BitLocker drive
  encryption both need a Pro licence.
- **Windows 10 without ESU.** No Windows security updates since October 2025. This note needs a zip
  from kit 2026-10-28.1 or later. Zips from older kits say nothing about ESU, so on them a missing
  note does not mean the laptop has ESU.

The kit does not require drive encryption or a file backup. The app's report still shows both.

## 5. The pass rules: agree them with your manager

The app grades health as Good, Normal or Bad. Whether a laptop is good enough for a new employee
is a business decision, so it is set in `audit-rules.json`. These defaults are proposals. Edit the
numbers in Notepad and run the summary again: the zips are never changed.

| Rule | Default | Why this default |
|---|---|---|
| `minRamGb` | 8 | Windows 11 runs on 4 GB, but Office, a browser and Teams together need 8 GB to stay usable. |
| `requireSsd` | true | Windows 11 on a hard disk is slow enough to waste an employee's time every day. Swapping in an SSD is cheap. |
| `minBatteryPct` | 60 | Below 60% of its design capacity a battery is in replacement territory, the same line the app uses. Lower it for laptops that stay on a desk. |
| `minSystemDriveFreePct` | 15 | Updates fail and SSDs slow down on a nearly full drive. Set it to 0 if every laptop is reinstalled before handover. |
| `requireWindows11Ready` | false | Decided on 27 October 2026: a laptop is not replaced only because Windows 11 does not support its processor. It gets Windows 10, where a company PC needs Microsoft's paid Extended Security Updates, or Windows 11, which Microsoft does not support on that hardware and whose updates it does not guarantee. The **Windows 11** column and the notes say which applies. Set it to true to go back to the old rules: such laptops become REPLACE, any other block (TPM, Secure Boot, a small system drive...) becomes FIX FIRST, and "Not assessed", "Verdict not interpreted" and "hardware support not read" become CHECK AGAIN. This also applies to a laptop that already runs Windows 11 on hardware Windows 11 does not support. |
| `driveWarningsNeedFix` | false | Drive *failures* always mean FIX FIRST. Early *warnings* are shown as notes; set this to true to treat them as failures too. |

**How Windows 11 readiness is checked:** the app reads the verdict Windows itself keeps, from the
same compatibility check Windows Update uses. It is not the app's own guess. "Not assessed" means
Windows never ran that check on the laptop, which some company policies turn off. "Verdict not
interpreted" means Windows gave a verdict the app does not read; the laptop's report shows it. On a
laptop already running Windows 11, the column says whether Microsoft supports Windows 11 on that
hardware, or "hardware support not read" when that was not read. Kits older than 2026-10-28.1 never
read it, so every Windows 11 laptop they checked reads "hardware support not read", and its note
says so.

## What the app does not check

Some things the app cannot measure, so the questions cover them:

- swelling, the screen, the keyboard and touchpad, ports, Wi-Fi, the camera and microphone, sound,
  the charger, the body and hinges, and firmware locks.

Some need longer tests than a 3-minute check:

- **a full memory test:** run Windows Memory Diagnostic (`mdsched`) when the report lists hardware
  errors (WHEA);
- **a full drive surface scan;**
- **a long stress test of the processor and graphics.**

## Troubleshooting

| What you see | What to do |
|---|---|
| "Windows protected your PC" | Click **More info**, then **Run anyway**. Unblocking the zip in step 1 avoids this. |
| "This laptop's clock says ..., before this kit was made", or "An earlier check of this laptop on this stick is dated ..." | The laptop's clock is wrong (its clock battery is probably flat). Set the date and time (**Settings > Time & language**) if you can. The result is kept either way, and with `Results\` kept on the stick the table still knows which check came last. |
| The antivirus quarantines `PcHealthCheck.exe` | The app is not signed yet. Ask whoever runs the antivirus to allow it, and send the details back. |
| Verdict CHECK AGAIN, "administrator prompt was declined" | Run it again and click **Yes**. Drive health needs administrator rights. |
| "Cannot write to ...\Results" | The stick is full or write-protected. The result goes to that laptop's Desktop instead: copy it off, then delete it. |
| "N USB drives are plugged in" | Unplug the extra drives and run the check again. |
| The window closes at once | Open the folder from the stick, not from inside the zip: the zip must be extracted first. |
