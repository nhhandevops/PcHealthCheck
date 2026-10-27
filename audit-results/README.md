# audit-results: put the laptop audit zips here

The result zips from the company laptop audit arrive on a USB stick, in its
`PcHealthCheck\Results\` folder. To review them on this computer:

1. `git pull`, so this computer has the latest code and notes.
2. Copy everything in the stick's `PcHealthCheck\Results\` into **this folder**: every `*.zip`,
   and every `.work-*` folder. A folder is a run whose window was closed before its zip was
   finished, and the summary reads it. A run closed after its answers may exist only as a folder.
3. Tell Claude: **"Review the audit results in audit-results/"**.

**Nothing in this folder except this README is ever committed.** `.gitignore` makes sure of it.
The zips hold serial numbers, computer names and Windows user names, and this repository is
public. Check with `git status`: the zips must not appear.

## For Claude: what to do with them

Follow "When result zips come back" at the top of `NEXT-STEPS.md`. In short:

1. Build the fleet table from the zips, without touching the stick's copy:
   `powershell -ExecutionPolicy Bypass -File audit-kit\summarize-fleet.ps1 -ResultsDir audit-results -RulesFile audit-kit\audit-rules.json`
2. Read the zips and `.work-*` folders in this order:
   - each run's `kit.json` `Stage` (anything but `complete` means the run stopped early, and
     `StoppedEarly` says if it was Ctrl+C or an error; zips made before KIT-007 have no `Stage` and
     are complete),
     `ScriptErrors` and `console-errors.txt`. The console files are UTF-8
     from the KIT-006 build on; zips from the `af47a36` and `462f176` builds hold them in code
     page 1252, so decode those as 1252;
   - modules that are not Completed in `report\pc-health-report.json`;
   - whether the verdicts make sense;
   - models the app has never seen before.
3. Fix each problem with a test, and log it in `docs/warning_bug_and_solutions.md`. Rebuild the kit with `build-release.ps1`.
4. **Write no serial number, computer name or user name into any committed file.** Describe a
   laptop by its model ("a Dell Latitude 5490") and the symptom instead.
5. Tell the owner which laptops to re-check, if any, before the rest of the fleet is done.
