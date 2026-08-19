# PC Health Check

One place to audit a Windows PC's hardware and software health — **offline by default, read-only by default, and nothing runs without your consent.**

Built with C# / .NET 10 + Avalonia. Windows 10/11 first; Linux/macOS planned via the per-OS module provider.

## What it checks today (M1 — no admin, no bundled tools, no network)

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

Every metric is graded **Good / Normal / Bad** by data-driven rules (`src/PcHealth.Core/Rules/*.json`) with concrete fix tips and a failure forecast where relevant.

## Privacy & safety design

- **No network code.** Every internet-touching feature ever added lives only in `PcHealth.Net.Optional` and runs only after an explicit, labeled, per-feature consent. CI fails the build if any other assembly mentions `System.Net`.
- **Read-only.** Modules never modify the system and never read your documents' contents. The only write in M1 is the battery report XML inside the app's own temp folder (declared on its consent row, deleted after parsing).
- **Consent matrix.** Each check is off until you enable it; rows show ADMIN / WRITES / INTERNET badges. Consent is stored per capability-hash — if an update changes what a module can do, you are asked again.
- **Receipt.** After every scan: every process spawned (full command line), every file written, and the standing "network connections: 0".
- **Unprivileged by design.** The UI runs `asInvoker` and never elevates itself. Admin-only checks (SMART, sensors…) arrive in M2 via a separate consented worker process — one UAC prompt per session, only when you select an admin-badged check.
- Reports export only via a save dialog, as self-contained HTML or JSON.

## Build & run

```
dotnet build PcHealthCheck.slnx
dotnet test  PcHealthCheck.slnx
dotnet run --project src/PcHealth.App                  # GUI
PcHealthCheck.exe --headless-scan --out <dir>          # headless scan → HTML+JSON report
```

Requires the .NET 10 SDK.

## Roadmap

- **M2** — elevated worker (single UAC prompt): SMART via bundled smartctl (GPL-2.0-or-later, invoked as a separate process), storage reliability counters, `Repair-WindowsImage` integrity check, TPM/Secure Boot/BitLocker status, backup posture (restore points, File History), SQLite trend history.
- **M3** — sensors (LibreHardwareMonitorLib + signed PawnIO driver, consent-gated install) and benchmarks (Microsoft DiskSpd read-only profile, 7-Zip CPU bench) with personal baselines and wear forecasting.
- **M4** — malware layers: Defender scan orchestration, bundled ClamAV second opinion (opt-in signature download), opt-in VirusTotal hash lookups; network health checks; packaging & code signing.
- **M5** — browser-extension audit, multi-user startup coverage, localization, Linux module provider.

## License

MIT for this project's own code. Planned bundled tools keep their own licenses (see `THIRD-PARTY-NOTICES.md`).
