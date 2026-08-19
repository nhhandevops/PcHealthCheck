# PC Health Check

One place to audit a Windows PC's hardware and software health — **offline by default, read-only by default, and nothing runs without your consent.**

Built with C# / .NET 10 + Avalonia. Windows 10/11 first; Linux/macOS planned via the per-OS module provider.

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

### Administrator checks — opt-in, one UAC prompt, still read-only

| Module | What it finds |
|---|---|
| Drive SMART health | The drive's own failure indicators: reallocated & pending sectors, uncorrectable errors, CRC (cable) errors, temperature, power-on hours |
| Drive wear & error counters | SSD endurance used, temperature and uncorrected read/write errors — the path that covers **NVMe** SSDs; plus each volume's dirty flag |
| Backup & recovery readiness | Restore points, shadow copies, File History, OneDrive folder backup — so "back up first" advice knows whether any backup exists |
| Platform security | TPM readiness, Secure Boot, drive encryption status (never keys), virtualization-based security |
| System file integrity (quick) | Whether Windows has recorded component-store corruption (reads the flag; repairs nothing) |

Every metric is graded **Good / Normal / Bad** by data-driven rules (`src/PcHealth.Core/Rules/*.json`) with concrete fix tips and a failure forecast where relevant.

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
- **M2.5** — SQLite trend history (alert when SMART counters *grow* between scans, project SSD endurance), and optionally bundling `smartctl` (GPL-2.0-or-later, separate process) for deeper per-vendor and NVMe attributes.
- **M3** — sensors (LibreHardwareMonitorLib + signed PawnIO driver, consent-gated install) and benchmarks (Microsoft DiskSpd read-only profile, 7-Zip CPU bench) with personal baselines.
- **M4** — malware layers: Defender scan orchestration, bundled ClamAV second opinion (opt-in signature download), opt-in VirusTotal hash lookups; network health checks; packaging & code signing.
- **M5** — browser-extension audit, multi-user startup coverage, localization, Linux module provider.

## License

MIT for this project's own code. Planned bundled tools keep their own licenses (see `THIRD-PARTY-NOTICES.md`).
