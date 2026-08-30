# Third-party notices

Everything third-party that this app uses or redistributes. Every licence below was read from the
package's own metadata (`.nuspec`) or its bundled `LICENSE` file, not from memory — a notices file
written from recollection is the one document where being approximately right is worthless.

## Libraries redistributed inside the download

Six native libraries sit **beside the exe in the zip**, so this project redistributes them and
their licences apply to what you download. They arrive as transitive dependencies of Avalonia,
Microsoft.Data.Sqlite and LibreHardwareMonitorLib rather than being chosen directly. With the exe
and the four documents, that is the eleven files the archive extracts to.

| File in the zip | From package | Licence |
|---|---|---|
| `libSkiaSharp.dll` | SkiaSharp.NativeAssets.Win32 3.119.4 | MIT |
| `libHarfBuzzSharp.dll` | HarfBuzzSharp.NativeAssets.Win32 8.3.1.3 | MIT |
| `av_libglesv2.dll` | Avalonia.Angle.Windows.Natives 2.1.27548.20260419 | BSD-3-Clause (ANGLE Project Authors) |
| `e_sqlite3.dll` | SQLitePCLRaw.lib.e_sqlite3 2.1.12 | Apache-2.0 |
| `MonoPosixHelper.dll`, `libMonoPosixHelper.dll` | Mono.Posix.NETStandard 1.0.0 | ⚠ see note below |
| `PcHealthCheck.exe` | this project, plus the managed dependencies below bundled into it | MIT |

**⚠ Mono.Posix.NETStandard needs confirming before this repository is made public.** Its package
declares no SPDX licence expression and ships no licence file — only a `licenseUrl` pointing at a
Microsoft redirect. It reaches us through LibreHardwareMonitorLib and is redistributed in the zip,
so the licence must be established rather than assumed. Verify with
`dotnet nuget why src/PcHealth.App/PcHealth.App.csproj Mono.Posix.NetStandard` and the package's
own repository before publishing.

## Managed dependencies

Compiled into `PcHealthCheck.exe` by the self-contained publish.

| Package | Version | Licence | Use |
|---|---|---|---|
| Avalonia (+ Desktop, Themes.Fluent, Fonts.Inter) | 12.1.1 | MIT | UI framework |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM source generators |
| System.Management | 10.0.11 | MIT | WMI/CIM queries |
| System.Diagnostics.EventLog | 10.0.11 | MIT | Event-log mining |
| Microsoft.Data.Sqlite | 10.0.11 | MIT | Trend history store |
| LibreHardwareMonitorLib | 0.9.6 | **MPL-2.0** | Temperature and fan sensors |

**LibreHardwareMonitorLib is the one non-MIT dependency**, and MPL-2.0 carries a real obligation:
the library is used **unmodified**, and its source is available at
<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>. It must stay at **0.9.5 or newer** —
0.9.4's bundled WinRing0 driver is quarantined by Windows Defender. It ships no kernel driver of
its own; CPU and motherboard sensors need the user to install PawnIO separately.

## Tools the app offers to install, and never ships

**Nothing in this table is bundled, and no third-party executable lives in this repository.** The
app offers to install each one through winget, shows the exact command before running it, records
what it installed in its own ledger, and offers to remove it again when you close the app. A tool
that was already on the PC is structurally unremovable by the app — it only ever removes what it
installed itself.

Because they are separate, unmodified programs invoked out-of-process, each keeps its own licence
and none of it propagates to this project.

| Tool | winget id | Licence | Why the app wants it |
|---|---|---|---|
| PawnIO | `namazso.PawnIO` | GPL-2.0 with linking exception | Kernel driver that makes CPU and motherboard temperatures readable |
| Microsoft DiskSpd | `Microsoft.DiskSpd` | MIT | The disk read benchmark |
| 7-Zip | `7zip.7zip` | LGPL-2.1-or-later | The single-core processor benchmark |

## Not used, and deliberately so

- **ClamAV** — planned as a third malware layer, **dropped by the project owner on 2026-08-20**.
  Two reasons, recorded in `HANDOFF.md`: the current build is itself a patch for five CVEs in
  parsers reachable by scanning a malformed file, which is exactly what it would be doing here; and
  the gap it filled is already covered by the Defender folder scan and the VirusTotal lookup.
- **smartmontools / `smartctl.exe`** — not bundled and not offered. SMART data is read through
  Windows' own `MSStorageDriver_FailurePredict*` WMI classes instead, which needs no extra binary
  and no GPL obligation.
- **Never bundled, because their licences or EULAs forbid redistribution:** Sysinternals tools,
  HWiNFO, Hard Disk Sentinel, CrystalDiskInfo art editions, Cinebench, FurMark.

## Fonts

Inter ships inside the exe via `Avalonia.Fonts.Inter` (MIT package). Inter itself is licensed under
the SIL Open Font License 1.1 — <https://github.com/rsms/inter>.
