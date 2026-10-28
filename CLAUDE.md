# PcHealthCheck

An offline-by-default, read-only-by-default PC health auditor (.NET 10 + Avalonia 12, Windows;
Linux provider phase 1). It grades hardware and software health Good / Normal / Bad with fix
tips, and prints a receipt of everything it touched.

**Start here, every session: [HANDOFF.md](HANDOFF.md).** It carries the current state, the
milestone table, and the gotchas that have each cost real time. Then [NEXT-STEPS.md](NEXT-STEPS.md)
for what is actually next and who has to do it — some rows need a person at the keyboard.

## The rules that must survive

These are not style preferences. Each one is here because breaking it shipped a bug.

1. **A check that could not run must never read as healthy.** A count of zero from a query that
   failed is not an all-clear. This project has shipped that bug at least four times; before
   adding any check, ask: *if this could not run, does the output still read as clean?*
2. **"I tried and it broke" degrades the scan. "I deliberately did not check this here" does
   not.** Over-correcting fires the "some checks could not finish" banner on healthy machines
   until nobody reads it, which is the worse bug. Test both directions, always.
3. **A threshold a healthy machine trips is a bug in the threshold.**
4. **Anything that exercises hardware must be measured against the app's own detectors** before
   shipping. The disk benchmark once created the very disk errors the app then graded Bad.
5. **A fix that improves a number is not proof the cause was found.** Isolate it.

## Before you commit

- **Never commit a serial number, a hardware UUID, a computer name or a Windows user name.** The
  repository is public. `audit-results/` is gitignored for this reason. Grep the staged diff for
  the serials in play, and for this machine's UUID (`Get-CimInstance Win32_ComputerSystemProduct`),
  before committing — a review agent once copied a real serial into a test fixture, and a real UUID
  nearly went the same way. A kit check fails on any UUID in the kit or its tests that is not a
  placeholder or obviously made up.
- **Build the release *after* committing.** The exe is stamped `1.0.0-rc.1+<HEAD>`, so a zip
  built on a dirty tree names a commit that does not contain the code inside it.
- Multi-line commit messages: write to a file and `git commit -F file`. PowerShell here-strings
  break on embedded quotes.

## Running things

```text
dotnet build PcHealthCheck.slnx -c Release -warnaserror
dotnet test  PcHealthCheck.slnx                                  # 783; 2 Linux-only, skipped on Windows
powershell -ExecutionPolicy Bypass -File ./tests/audit-kit/kit-tests.ps1   # 285 more, invisible to dotnet test
powershell -ExecutionPolicy Bypass -File ./build-release.ps1     # tests, privacy gate, publish, kit, zip, SHA-256
powershell -ExecutionPolicy Bypass -File ./daily-smoke.ps1       # "do the daily smokes"
```

`daily-smoke.ps1` covers the kit checks too, so it is the one command that runs everything.
**It refuses to run its scan step while DiskSpd is installed** — the disk benchmark would write
to every physical drive, backup drives included — and reports `Scan: SKIP`. On a machine with
DiskSpd that is permanent, so a green smoke there does not mean a scan ran. Which machine is in
which state is in [HANDOFF.md](HANDOFF.md) under "The two machines".

The audit kit (`audit-kit/*.ps1`) is **Windows PowerShell 5.1 and ASCII only** — 5.1 reads a
BOM-less script in the ANSI code page, so a literal em dash is garbled on some laptops. Build one
by code point when you need to match the app's output.

Analyzers CA1305/CA1304/CA1310 are **errors**: parse and format with an explicit culture.

## Where things are written down

- [docs/warning_bug_and_solutions.md](docs/warning_bug_and_solutions.md) — build, packaging and
  tooling faults no unit test covers. Add an entry for every fault found; include the wrong first
  guess, because that is usually the useful part.
- [HANDOFF.md](HANDOFF.md) — design decisions and what each milestone shipped.
- [ACCEPTANCE.md](ACCEPTANCE.md) — the rows to run before giving the app to anyone.
- [AUDIT-GUIDE.md](AUDIT-GUIDE.md) — for the person carrying the kit to a laptop, not for a
  developer.

## Two things a fresh clone does not give you

- **`release/` is gitignored.** Run `build-release.ps1` to make the kit zip; it is never committed.
- **`audit-results/` is empty except its README.** Laptop audit zips arrive on a USB stick, by
  hand. No amount of pulling brings them.
