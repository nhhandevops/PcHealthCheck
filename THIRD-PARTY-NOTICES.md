# Third-party notices

## NuGet dependencies (M1)

| Component | License | Use |
|---|---|---|
| Avalonia (+ Desktop, Fluent theme, Inter fonts) | MIT | UI framework |
| CommunityToolkit.Mvvm | MIT | MVVM source generators |
| System.Management | MIT (.NET Foundation) | WMI/CIM queries |
| System.Diagnostics.EventLog | MIT (.NET Foundation) | Event-log mining |

## Planned bundled tools (M2+)

These ship as **separate, unmodified executables** invoked out-of-process (GPL "mere aggregation"); each keeps its own license, whose full text will ship alongside the binary together with a source link pinned to the exact bundled version:

| Tool | License | Obligation checklist |
|---|---|---|
| smartmontools (`smartctl.exe` + drivedb) | GPL-2.0-or-later | Ship GPL text; link to source of the exact bundled release; separate process only |
| Microsoft DiskSpd | MIT | Ship MIT notice |
| 7-Zip (`7za.exe`) | LGPL-2.1+ | Ship LGPL text + source pointer |
| LibreHardwareMonitorLib | MPL-2.0 | Unmodified DLL; ship MPL notice + its third-party notices |
| PawnIO driver | GPL-2.0 + linking exception | Unmodified official signed installer; ship license + source link |
| ClamAV | GPL-2.0 | Separate process only (never link libclamav); ship GPL text + source link |

**Never bundled** (licenses/EULAs forbid redistribution): Sysinternals tools, HWiNFO, Hard Disk Sentinel, CrystalDiskInfo art editions, Cinebench, FurMark.
