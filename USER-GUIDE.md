# PC Health Check — User Guide

A health check for your Windows PC. It looks at the drives, the processor, the sensors, the
security settings, what starts with Windows, and what your antivirus has already caught, and
tells you in plain words what is fine, what to watch, and what to do about it.

Two things shape everything below:

- **It reads. It does not fix.** With one exception you choose explicitly (the disk speed
  test, which writes its own temporary file), the app never changes anything on your PC. It
  will tell you what to run to fix something, and leave the running to you.
- **Nothing leaves your PC unless you send it.** There is exactly one internet feature, it is
  off unless you switch it on, and it shows you exactly what it would send first.

---

## 1. Getting started

### Install

There is no installer.

1. Extract the zip anywhere you like — your Desktop, a folder, a USB stick.
2. Double-click **PcHealthCheck.exe**.

To uninstall, delete the folder. Nothing is left behind in Program Files or the registry.

### "Windows protected your PC"

The first time you run it, Windows will probably show a blue SmartScreen box. This happens to
every program that has not been code-signed, which costs money and is still pending here.

If you trust where you got this from: click **More info**, then **Run anyway**.

If you would like to check first, the release comes with a `SHA256SUMS.txt`. Compare it:

```powershell
Get-FileHash .\PcHealthCheck.exe -Algorithm SHA256
```

If the number matches the one in the file, you have the exact build that was published and
nothing has been altered in between.

### The first screen

Nothing runs until you say so. You will see a list of checks, each with a checkbox, a plain
description of what it looks at, and what it reads. The list is in two groups:

- **Runs without administrator** — no prompts, no special rights.
- **Needs administrator** — selecting any of these adds **one** Windows prompt.

Press **Select recommended** to tick everything, or pick individually. Then **Scan now**.

---

## 2. Reading the result

### The overall grade

| Grade | What it means |
|---|---|
| **GOOD** | Nothing to do. |
| **NORMAL** | Worth knowing about; act when convenient. |
| **ACTION NEEDED** | Something is wrong and it will not fix itself. |
| **NOT MEASURED** | Nothing could be graded — usually because only informational checks ran. |

The overall grade is the worst grade found. One red result makes the whole PC red, which is
deliberate: a failing drive is not averaged away by everything else being fine.

### "Some checks could not finish"

If you see this amber box above the results, **read it**. It means a check started and could
not complete — a query failed, a file was locked, an account could not be opened.

The results that check would have produced are simply missing. That is not the same as those
results being good. The box names each one and why.

### INFO rows

Rows marked **INFO** have no grade. That is on purpose, not an omission. Some things cannot
honestly be scored:

- A processor benchmark score means nothing except against the same processor.
- An external drive's speed is set by its enclosure, not by the drive.
- A browser extension that reads every page you visit is a password manager working
  correctly, or spyware. The app lists what each one *can* do and lets you recognise it.

### "What changed since your last scan"

The most useful part of this app, and the part that needs you to run it more than once.

A single reading rarely tells you much. A drive that has had two bad sectors for two years is
fine; a drive that grew two bad sectors this week is failing. Once there is history, this
section reports what moved — and a counter that is climbing is treated as serious no matter
how small the number is.

Leave **Remember results to spot trends** ticked. It stores only measurement names and
numbers, in the app's own folder, and never leaves your PC. **Delete history** erases it.

---

## 3. The checks

### Runs without administrator

| Check | What you learn |
|---|---|
| System overview | Windows build, whether it is still supported, uptime, pending reboot |
| Volumes & free space | Free space per drive, file-system health, TRIM |
| Security posture | Whether antivirus is on and current, firewall, UAC, SmartScreen, Defender exclusions |
| Startup programs | Everything that starts with Windows, and whether each is properly signed |
| Windows license | Activation, genuine check, and whether a fake activation server is configured |
| Windows Update health | How long since the last successful update; repeated failures |
| Error history | Disk errors, hardware errors, unexpected shutdowns, blue screens, app crashes |
| Reliability index | Windows' own stability score and its trend |
| Device problems | Devices Windows reports as not working |
| Battery | Wear against design capacity (laptops) |
| Clock synchronisation | Clock drift, which quietly breaks updates and secure websites |
| Crash dump settings | Whether a blue screen would actually leave anything to diagnose |
| Malware history & defences | What your antivirus has caught, and whether anything was left unhandled |
| Network settings & integrity | DNS, hosts file tampering, proxy settings |
| Browser extensions | Every extension and what it can reach |
| Disk speed | Read speed per drive (see the warning below) |
| Processor speed | How much work one core gets through |

### Needs administrator

| Check | What you learn |
|---|---|
| Drive health (SMART) | The drive's own failure counters — the closest thing to an early warning |
| Storage reliability | Read/write error counters, wear, temperature |
| Backup posture | Whether any backup or restore point actually exists |
| Platform security | TPM, Secure Boot, BitLocker (protector types only — never your keys) |
| Component store health | Whether Windows' own files are intact |
| Temperatures & fans | CPU/GPU temperatures and fan speeds |
| Startup for other accounts | What starts for the other Windows accounts on this PC |

### The one check that writes

**Disk speed** is the only check that writes to your drives. It creates one test file per
drive (1 GB, or 256 MB on external drives), reads it back, and deletes it — including if you
cancel or it fails. The measured part only ever reads.

Two things worth knowing:

- Drives in USB enclosures often log I/O retries in Windows' event log while a large file is
  being written to them. If your disk-error count rises right after a benchmark, check the
  timestamps before blaming the drive — some of them may be from the test itself. The app
  says so in its own notes.
- A drive Windows already reports as failing is **skipped**. A drive in that state should be
  backed up and left alone, not measured.

---

## 4. Things you start yourself

These never run as part of a scan.

### Scan for malware now

- **Quick scan** — Windows Defender checks memory, startup locations and the usual hiding
  places. Two to fifteen minutes, no administrator rights. If it finds something it deals
  with it using your normal Defender settings.
- **Scan a folder** — checks one folder you choose, including inside .zip and .rar archives,
  which a normal scan skips. Runs with remediation off: anything found is reported and left
  exactly where it is.

Both ask Defender to do the work. The app makes no connection of its own, but Defender's own
cloud protection may contact Microsoft during a scan, exactly as it would if you started the
scan from Windows Security. The consent dialog says so before you agree.

### Second opinion from VirusTotal — the only internet feature

Asks around 70 antivirus engines about the programs that start with Windows.

- You need your own free API key from virustotal.com. None is built in, and the key is kept
  in memory for the session only — never written to disk.
- **Only a SHA-256 fingerprint of each file is sent. Your files are never uploaded.** A
  fingerprint does tell VirusTotal that your PC holds a file with those exact bytes, which is
  why the dialog lists every one before sending.
- Least-trusted files go first, and the list is capped at 20 because the free tier allows
  four lookups a minute. The dialog says how many were left unchecked.

Two rules it will not break: a file VirusTotal has never seen is **not** the same as clean,
and a couple of detections out of seventy is more often a false positive than malware.

### Optional tools

Some checks need a small free tool. When one is missing you get a purple banner offering to
install it, showing the exact command first:

| Tool | Enables |
|---|---|
| PawnIO | CPU and motherboard temperatures |
| Microsoft DiskSpd | Disk speed |
| 7-Zip | Processor speed |

Because the app installed them, it offers to remove them again when you close it — but only
after checking that they were not already on your PC and that nothing is using them. It never
removes anything it did not install. Every removal is logged with the command to get it back,
kept for 15 days.

---

## 5. Exporting and sharing a report

**Export HTML report** produces a single self-contained file you can open in any browser,
email, or send to whoever helps you with your PC. **Export JSON** is the same data for
scripting.

Before you send one, know what is in it: drive models and serial-style identifiers, volume
names, the names of your startup programs and browser extensions, installed account names,
and your PC name. There are no file contents, no passwords, no BitLocker keys and no product
keys. It is a hardware and configuration report — but it does describe your machine.

The report ends with a receipt: every external program the scan ran with its full command
line, every file written, and the number of network connections (zero, for a scan).

---

## 6. Running it without the window

For scheduling or scripting:

```powershell
PcHealthCheck.exe --headless-scan --out C:\reports
PcHealthCheck.exe --headless-scan --include-admin --out C:\reports   # one UAC prompt
PcHealthCheck.exe --headless-scan --no-history --out C:\reports      # record nothing
```

Writes `pc-health-report.html` and `pc-health-report.json` to the folder you name.

---

## 7. If something goes wrong

| What you see | What it means |
|---|---|
| SmartScreen warning on first run | Expected — the app is not code-signed yet. See §1. |
| A check says "Skipped" | It chose not to run: no battery on a desktop, a tool not installed. The note says which. |
| A check says "Failed" or "Partially completed" | It tried and could not finish. The amber box says why. Those results are missing, not good. |
| CPU temperature missing | Needs PawnIO — accept the banner's offer, or install it yourself. |
| Disk or processor speed skipped | Needs DiskSpd or 7-Zip. Same banner. |
| Nothing happens after Scan | Check that at least one box is ticked; the Scan button stays disabled until then. |
| It says my drive is failing | Back it up **first**, before any troubleshooting. Then read the Storage section for the specific counter. |

---

## 8. What this app cannot tell you

Worth knowing before you rely on it.

- **A green result is not a guarantee.** Roughly a quarter of drive failures give no SMART
  warning at all. Keep backups: three copies, on two kinds of media, one somewhere else.
- **It is not an antivirus.** It reads what Defender already knows and can ask Defender to
  scan. It does not detect malware itself.
- **It only sees this Windows account** unless you use the administrator checks, and even
  then another account's registry can only be read while that person is signed in.
- **Firefox add-ons are not covered yet.** Chrome, Edge, Brave, Vivaldi and Opera are.
- **Benchmarks are affected by whatever else the PC is doing.** A backup or a game updating
  during a scan will lower the numbers. Re-run on an idle PC before believing a drop.
