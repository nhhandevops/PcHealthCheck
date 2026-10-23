# PC Health Check

One place to audit a Windows PC's hardware and software health — **offline by default, read-only by default, and nothing runs without your consent.**

Built with C# / .NET 10 + Avalonia. Windows 10/11 first. Linux checks are in development: the modules and their tests exist, but there is no Linux build to run yet. macOS is planned via the same per-OS module provider.

> **⚠ Status: release candidate, not yet fully tested or widely used.** It has been run on only a few machines: one
> Windows 10 desktop and one Windows 10 laptop, mostly in English. Several acceptance checks are still open
> (see `ACCEPTANCE.md`), and it has not been tried on Windows 11, ARM PCs or a Vietnamese-language Windows. **Bugs and
> wrong readings may occur.** The app is read-only by default and tells you before it changes anything, but treat its
> grades as a second opinion, not a diagnosis, and keep your own backups. The binaries are not code-signed yet, so
> Windows SmartScreen will warn on first run. Issues and results from other machines are welcome.

## What it checks today

### Standard checks — no admin rights, no bundled tools, no network

| Module | What it finds |
|---|---|
| System overview | Windows build & support status (incl. the Windows 10 Oct 2026 end-of-support), uptime, pending reboot |
| Volumes & free space | Free-space bands per volume, file-system health, TRIM state |
| Security posture | Antivirus active & current (any vendor), Defender signature age & tamper protection, exclusion audit, firewall per profile, UAC, SmartScreen |
| Startup programs | Everything that starts with Windows, offline Authenticode verification (unsigned / **broken** signatures), Winlogon persistence check |
| Windows license | Activation state, genuine check (local API), license channel, localhost-KMS crack detection |
| Windows Update health | Days since last successful update, pending updates (local cache only), repeated failures |
| Error history | Disk I/O errors, WHEA hardware errors, unexpected shutdowns, blue screens, GPU driver resets, app-crash frequency |
| Reliability index | Windows' own 1–10 stability score + 30-day trend |
| Devices & drivers | Device Manager problem codes |
| Battery health | Wear (full-charge vs design) & cycles — skipped on desktops |
| Clock sync | Time-service state (drift breaks TLS/updates/activation) |
| Crash dump settings | Dumps enabled + page file present (so future BSODs are diagnosable) |
| Malware history & defences | Threats the antivirus caught, **anything left unresolved**, scan recency, and whether behaviour monitoring / download scanning / network protection are on |
| Network settings & integrity | Wired link speed (the classic bad-cable symptom), **hosts-file redirections** (malware blocks antivirus and update servers this way), proxy hijacking, DNS servers in use |

### Administrator checks — opt-in, one UAC prompt, still read-only

| Module | What it finds |
|---|---|
| Drive SMART health | The drive's own failure indicators: reallocated & pending sectors, uncorrectable errors, CRC (cable) errors, temperature, power-on hours |
| Drive wear & error counters | SSD endurance used, temperature and uncorrected read/write errors — the path that covers **NVMe** SSDs; plus each volume's dirty flag |
| Backup & recovery readiness | Restore points, shadow copies, File History, OneDrive folder backup — so "back up first" advice knows whether any backup exists |
| Platform security | TPM readiness, Secure Boot, drive encryption status (never keys), virtualization-based security |
| System file integrity (quick) | Whether Windows has recorded component-store corruption (reads the flag; repairs nothing) |
| Temperatures & fans | CPU and GPU temperatures (incl. hot spot and memory junction where the card exposes them), fan speeds, memory use, and a stopped-fan-while-hot check. **Loads a kernel driver** — see below |

#### The one thing that changes your system

Motherboard and CPU sensors cannot be read from user mode, so the temperature module loads a kernel-level driver (LibreHardwareMonitor's, via `LibreHardwareMonitorLib` ≥ 0.9.5, which uses the signed driver rather than the WinRing0 build that antivirus vendors began quarantining in 2025). Because that is a genuine system change, it does not hide behind the "needs admin" badge:

- modules declare a `LoadsDriver` capability that is part of the consent hash, so older consent can never silently cover it;
- the consent matrix shows a distinct **LOADS DRIVER** badge;
- the elevated helper asks about it in its own dialog and simply skips the module if you decline.

The driver is used to read values only — never to set fan curves, clocks, or voltages.

#### Installing and removing optional tools

Some readings need an extra tool (today: the PawnIO sensor driver). The app manages these with the same consent-first rules as everything else:

- **Before installing**: the app shows what the tool is for and the **exact command** it will run (e.g. `winget install namazso.PawnIO`), labeled as needing administrator rights and the internet. Nothing happens on "Don't install".
- **When you close the app**: if the app itself installed a tool, it offers to remove it again. Before removing it checks two things — that the tool was **not already on the PC** before this app installed it (tracked in `dependencies.json`; pre-existing tools are never offered for removal), and that **no running program is using it** (e.g. FanControl, OpenRGB). If something is using it, the app refuses and tells you why.
- **Every removal is logged** to `%LOCALAPPDATA%\PcHealthCheck\dependency-history.txt` with the reinstall command, kept for **15 days**, so getting a tool back is one copy-paste.

Every metric is graded **Good / Normal / Bad** by data-driven rules (`src/PcHealth.Core/Rules/*.json`) with concrete fix tips and a failure forecast where relevant.

### Trends — what a single scan cannot tell you

A snapshot cannot distinguish a drive that has had 2 bad sectors for two years from one that grew 2 bad sectors this week. The second is an emergency. So each scan records its numbers to a local SQLite file (`%LOCALAPPDATA%\PcHealthCheck\history.db`) and the next scan reports what moved:

- **Failure counters that grow** (reallocated/pending sectors, uncorrectable errors, CRC errors) are raised to *action needed* regardless of how small the number is, with back-up-first advice.
- **Wear figures** (SSD endurance, battery capacity) are extrapolated once there are at least 14 days of data, giving a projected date for reaching end of rated life.

It stores only metric names, subjects, and numbers — never file contents. Turn it off with the checkbox in the UI (or `--no-history` on the command line), and delete it with the **Delete history** button.

## Privacy & safety design

- **No network code.** Every internet-touching feature ever added lives only in `PcHealth.Net.Optional` and runs only after an explicit, labeled, per-feature consent. CI fails the build if any other assembly mentions `System.Net`.
- **Read-only.** Modules never modify the system and never read your documents' contents. The only write in M1 is the battery report XML inside the app's own temp folder (declared on its consent row, deleted after parsing).
- **Consent matrix.** Each check is off until you enable it; rows show ADMIN / WRITES / INTERNET badges. Consent is stored per capability-hash — if an update changes what a module can do, you are asked again.
- **Receipt.** After every scan: every process spawned (full command line), every file written, and the standing "network connections: 0".
- **Unprivileged by design.** The UI runs `asInvoker` and never elevates itself. Admin checks run in a *separate* short-lived helper process (`--worker`), started through the UAC prompt only when you selected an admin-badged check — one prompt per scan, and the helper exits when the scan ends.
- Reports export only via a save dialog, as self-contained HTML or JSON.

### How the privilege split works

The UI creates a private, randomly-named pipe, launches one elevated copy of the same exe, and verifies that the process which connected is exactly the one it started. The elevated helper then treats everything arriving over that pipe as an untrusted *request*: it only runs modules that exist in its own compiled-in registry and that declare `NeedsAdmin`, so nothing an unprivileged process writes to disk (including `consent.json`) can widen what the admin half does. Every module in that registry is read-only; if a future one declares a write, the helper asks you itself, on its own desktop, before doing it.

## Build & run

```text
dotnet build PcHealthCheck.slnx
dotnet test  PcHealthCheck.slnx
dotnet run --project src/PcHealth.App                            # GUI
PcHealthCheck.exe --headless-scan --out <dir>                    # standard checks → HTML+JSON report
PcHealthCheck.exe --headless-scan --include-admin --out <dir>    # also the admin checks (one UAC prompt)
```

Requires the .NET 10 SDK.

## Roadmap

- ~~**M1** — standard checks, verdict engine, consent matrix, reports.~~ Done.
- ~~**M2** — elevated worker (single UAC prompt), SMART attributes, storage reliability counters, integrity check, platform security, backup posture.~~ Done.
- ~~**M2.5** — SQLite trend history: alert when SMART counters *grow* between scans, project SSD endurance and battery wear to a date.~~ Done.
- **Next** — optionally bundle `smartctl` (GPL-2.0-or-later, separate process) for deeper per-vendor and NVMe attributes.
- **M3** — sensors (LibreHardwareMonitorLib + signed PawnIO driver, consent-gated install) and benchmarks (Microsoft DiskSpd read-only profile, 7-Zip CPU bench) with personal baselines.
- **M4 (part 1 done)** — malware history & defences, network/hosts/proxy integrity. Remaining: on-demand Defender scan orchestration, ClamAV second opinion (offered via the dependency manager), opt-in VirusTotal hash lookups, packaging & code signing.
- **M5** — browser-extension audit, multi-user startup coverage, localization, Linux module provider (phase 1, unelevated readings and tests, done 2026-10-23; a Linux command-line build is next).

## License

MIT for this project's own code. Planned bundled tools keep their own licenses (see `THIRD-PARTY-NOTICES.md`).
