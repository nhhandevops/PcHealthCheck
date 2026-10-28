# Weekly report: 23-28 October 2026

PC Health Check checks the company's laptops before they go to new employees. This week it moved
from a pilot to a working audit tool, and the app learned to say what to do about what it finds.
Nothing in this report names a laptop, a serial number or a person.

## 1. The laptop audit

- **Pilot, 23 October.** Three ThinkPads were checked with the USB kit: a T470p and two T470s.
  - Verdicts: all three need work. The T470p's SSD is failing, one T470s has a dead battery
    (4.8% of design), and the other has a faulty keyboard.
  - None of the three can officially run Windows 11 (7th-generation processors).
- **Reviewing the pilot results** found eight faults in the kit and the app (KIT-001 to KIT-008),
  all fixed. The biggest:
  - closing the kit's window at the questions lost a finished scan (KIT-007);
  - disk errors from the auditor's own USB drive were counted against the laptop (KIT-005).
- **The audit stick** (a 1 TB Seagate in a USB case) carries the current build, `2071660`. Its
  `Results` folder is unchanged: 3 pilot zips and 1 test zip.
  - Windows logged some disk errors on it. They turned out to come from plugging it in, not
    from its surface, and Error Checking passed.
  - Rule kept: copy `Results` off the stick after every batch.

## 2. Decisions made by the owner (27 October)

Recorded in `docs/REMEDIES-PLAN.md`, section 6:
- Every laptop is reimaged from a disk image before handover.
- Four apps on every laptop: Chrome, Unikey, LightShot, NetBird. They are also the startup
  allowlist.
- The app never deletes the user's or Windows' files.
- Both Windows 10 and Windows 11 will be used. "Cannot run Windows 11" no longer means REPLACE.
- Drive encryption and file backup are not required. BitLocker is available on request, with a
  guide.
- English first, Vietnamese later.
- Testing is done in a virtual machine, then on spare laptops.

## 3. What was built

**Phase 1 Step A, released 28 October (`2071660`, on the stick)**:
- **The truth about Windows support.** The app had called an unenrolled Windows 10 company laptop
  "supported until 2027-10-12", which is the consumer ESU date (OS-001). It now reads the ESU
  licence. On a Windows 10 laptop without paid Extended Security Updates, the app correctly says
  "no security updates since October 2025".
- **False alarms removed.** These fired on every PC or on healthy ones:
  - Windows Security's own icon counted as "unsigned";
  - a stopped time service;
  - a new install's empty history;
  - USB drives counted as the laptop's disk;
  - SSD temperatures graded on hard-disk limits.
- **Safety.** The disk speed test skips any drive the scan itself calls failing.
- **Privacy.** Reports no longer name other Windows accounts or show their commands.
- **The kit.** A processor Windows 11 refuses is now a note, not REPLACE, so the three pilot
  laptops move to FIX FIRST for their real hardware faults.

**Phase 1 Step B, built 28 October: committed, not yet released to the stick.**
- **A "What you can do" section** in the app window, the HTML report, the JSON and the headless
  output. At most five cards, then "N more", plus "Don't show again".
- **The advice, each with this PC's own numbers:**
  - "Back up this drive now", for a failing internal drive, with a copy-time estimate and a
    replacement size;
  - security settings switched off;
  - Windows 11 blocked only by a BIOS switch;
  - C: low on space, with clean-up steps through Windows' own tools;
  - runs hot and slows itself down;
  - use Restart, not Shut down, when Windows has run for days.
- **No noise.** A healthy PC, or a freshly reimaged one, gets no cards. A check that could not run
  shows "not measured" instead of an all-clear.
- **On the pilot results,** only the T470p's failing SSD gets a card.

**Plans written:**
- `docs/REMEDIES-PLAN.md`: the full plan for advice and guided fixes.
- `docs/GOLDEN-IMAGE-PLAN.md`: how to build, check and restore the company's Windows images, with
  ten owner decisions (A-J) and a BitLocker guide.
- `docs/STEP-B-SPEC.md`: the Step B build spec.

## 4. How it was checked

- **Agents in sequence, then review.** Every step was built by agents working one after another,
  then reviewed from several angles, with a skeptic re-checking each finding.
  - Step A's review confirmed 22 faults; Step B's confirmed 12, plus 1 that started as uncertain.
  - All were fixed, each with a test, and a final gate re-ran everything.
- **Tests.** 1205 app tests pass (2 Linux-only skipped), and so do 286 kit checks. Before this week
  there were 457 and none.
- **Every fault is in `docs/warning_bug_and_solutions.md`,** with its wrong first guess.
- **Live runs on the T470p.**
  - An elevated scan of the Step A build passed, except for one drive check that timed out with
    four USB devices attached.
  - An unelevated scan of Step B shows no cards on this healthy PC.

## 5. Still open

| Item | Who | Notes |
|---|---|---|
| Release Step B and put it on the stick | Claude, next session | `build-release.ps1` after the commit, then copy onto the stick keeping `Results` |
| An elevated scan of the Step B build (one UAC click) | owner + Claude | Confirms the drive readings and the new disk rows carry the same disk numbers. The first try was not approved (the prompt was missed), which at least proved the "not measured" path works |
| ACCEPTANCE rows K1-K12 (the new section in the app window) | owner | Cards, "N more", "Don't show again", "Show again". K2 and K7 are blocking |
| A scan on the Windows 11 desktop | owner | Nothing new has run on Windows 11 yet |
| Golden image decisions A-J, licensing first | owner | `docs/GOLDEN-IMAGE-PLAN.md` |
| Buy commercial ESU for the Windows 10 laptops? | owner | The only open question in the remedies plan |
| REP-002: some report lines show "0,8" instead of "0.8" on a Vietnamese number format | Claude | About 30 lines, to fix as one class |
| The drive reliability check timed out with four USB devices attached | Claude | Not caused by Step A. Find which USB bridge hangs |
| TOOL-005: an incremental build can report success right after a failed `-warnaserror` build | Claude | A fix for `daily-smoke.ps1` is proposed, not applied |
| Steps C, D (intake and handover check), E (Vietnamese) | Claude, after the owner's go | Multi-agent work. Best started after the weekly usage limit resets |

## 6. Where to start next week

Open `HANDOFF.md` and read "Start here next week" at the top of "Suggested next session". The
first three actions are:
1. Build the release.
2. Run the elevated scan.
3. Copy the build to the stick.
