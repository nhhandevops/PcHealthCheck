using System.Text.RegularExpressions;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Free space and health status per fixed volume, plus TRIM state for SSDs, and, for information,
/// where each attached disk is and the space in use on it.
/// </summary>
internal sealed partial class VolumesModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "volumes",
        Name = "Volumes & free space",
        Description = "Free space and reported health per fixed volume; whether TRIM is enabled for SSDs. A USB drive's free space is shown but does not count toward this PC's health.",
        Reads = ["Volume list, sizes and health (WMI)", "Which disk each volume is on, and each disk's connection and type (WMI)", "TRIM setting (fsutil, read-only query)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(3),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = 0;

        // On the pilot, the only "disk nearly full" on three laptops was the auditor's own USB
        // drive (REMEDIES-PLAN D1). Its free space is still shown, for information. When the
        // connections cannot be read every volume is graded as before: a false alarm on a USB
        // drive costs less than a full internal drive going unmentioned.
        var map = DriveMap.Read(out var mapError);
        if (map is null)
        {
            notes.Add($"Which drives are plugged in over USB could not be read ({mapError}), so the free space of " +
                      "every drive is graded, USB drives included.");
        }

        var volumes = Wmi.TryQuery(@"root\Microsoft\Windows\Storage",
            "SELECT DriveLetter, FileSystemLabel, DriveType, HealthStatus, Size, SizeRemaining FROM MSFT_Volume",
            out var msftError);
        var measured = new List<(string Letter, ulong Size, ulong Free)>();

        if (msftError is null && volumes.Count > 0)
        {
            foreach (var vol in volumes)
            {
                if (Wmi.Get<uint>(vol, "DriveType") is not 3) continue; // fixed disks only

                var letterRaw = Wmi.GetString(vol, "DriveLetter")?.Trim('\0', ' ');
                if (string.IsNullOrEmpty(letterRaw)) continue; // unmounted system partitions
                var label = Wmi.GetString(vol, "FileSystemLabel");
                var subject = string.IsNullOrWhiteSpace(label) ? $"{letterRaw}:" : $"{letterRaw}: ({label})";

                var size = Wmi.Get<ulong>(vol, "Size");
                var remaining = Wmi.Get<ulong>(vol, "SizeRemaining");
                if (size is > 0 && remaining is not null)
                {
                    measured.Add((letterRaw, size.Value, remaining.Value));
                    AddFreeSpaceMetric(metrics, subject, size.Value, remaining.Value, map?.ExternalBusOf(letterRaw));
                }

                if (Wmi.Get<ushort>(vol, "HealthStatus") is { } health)
                {
                    metrics.Add(new Metric("storage.volume.healthy", "Volume health", health == 0,
                        HealthCategory.Storage, Subject: subject,
                        Detail: health switch
                        {
                            0 => "File system reports Healthy.",
                            1 => "File system reports: scan needed.",
                            2 => "File system reports: spot fix needed.",
                            3 => "File system reports: full repair needed.",
                            _ => $"File system health state {health}.",
                        }));
                }
            }
        }
        else
        {
            // The fallback reports free space but NOT the file system's own health flag, so
            // a volume that needs repair would go unmentioned. That is lost coverage, not a
            // clean result, and the module has to say so rather than quietly reporting less.
            if (msftError is not null)
            {
                notes.Add($"MSFT_Volume unavailable ({msftError}); fell back to Win32_LogicalDisk, which reports " +
                          "free space but not file-system health — so volume health was NOT checked this time.");
                failures++;
            }
            foreach (var disk in Wmi.TryQuery(@"root\cimv2",
                "SELECT DeviceID, VolumeName, FreeSpace, Size FROM Win32_LogicalDisk WHERE DriveType = 3", out var ldError))
            {
                var subject = Wmi.GetString(disk, "DeviceID") ?? "?";
                var size = Wmi.Get<ulong>(disk, "Size");
                var free = Wmi.Get<ulong>(disk, "FreeSpace");
                if (size is > 0 && free is not null)
                {
                    measured.Add((subject, size.Value, free.Value));
                    AddFreeSpaceMetric(metrics, subject, size.Value, free.Value, map?.ExternalBusOf(subject));
                }
                if (ldError is not null) { notes.Add($"LogicalDisk query: {ldError}"); failures++; break; }
            }
        }

        // TRIM is a setting only SSDs use, so a PC whose drives are all hard disks gets no TRIM row
        // (REMEDIES-PLAN H7). Only a definite answer leaves it out: a disk that did not say what it
        // is, or a list that could not be read, keeps the row as before. A deliberate non-check, so
        // a note that does not degrade the module.
        var physical = Wmi.TryQuery(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk", out var physicalError);
        if (physicalError is null &&
            DiskBus.AnyInternalSsd(physical.ConvertAll(d => (Wmi.Get<ushort>(d, "MediaType"), Wmi.Get<ushort>(d, "BusType")))) == false)
        {
            notes.Add("Every drive inside this PC is a hard disk, so TRIM, a setting only SSDs use, was not checked.");
        }
        else
        {
            failures += await AddTrimStateAsync(context, metrics, notes, ct).ConfigureAwait(false);
        }

        return DiskRoles.AddAfterStatus(metrics, failures, map, measured);
    }

    /// <summary>TRIM state, from a read-only fsutil query. Returns 1 when the read broke, 0 otherwise.</summary>
    private async Task<int> AddTrimStateAsync(ModuleContext context, List<Metric> metrics, List<string> notes, CancellationToken ct)
    {
        var trim = await context.Processes.RunAsync(Manifest.Id, "fsutil", "behavior query DisableDeleteNotify",
            TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
        if (trim.Succeeded)
        {
            // Locale-tolerant: key off the stable token "DisableDeleteNotify" and the digit; NTFS line first.
            var match = TrimStateRegex().Match(trim.StdOut);
            if (match.Success)
            {
                var trimEnabled = match.Groups[1].Value == "0"; // 0 = delete notifications (TRIM) enabled
                metrics.Add(new Metric("storage.trim.enabled", "TRIM enabled (SSD)", trimEnabled, HealthCategory.Storage,
                    Detail: "TRIM lets SSDs reclaim deleted blocks; disabled TRIM degrades SSD performance and lifespan."));
                return 0;
            }

            // A parse that fails on a non-English Windows is exactly the locale trap this
            // codebase already has analyzers for; it must not pass as "TRIM is fine".
            notes.Add("Could not interpret the TRIM setting from fsutil on this system's language, so TRIM " +
                      "was NOT checked. It is not known whether it is on or off.");
            return 1;
        }

        // Conditional now, because the sentence that stood here was the same unchecked
        // diagnosis as SMART-001: it blamed missing administrator rights and promised the
        // elevated scan would cover it, without checking either. This module is NOT an admin
        // module, so it runs in whatever process the user started - and when that process is
        // already elevated there is no separate elevated half to defer to, both halves of the
        // old sentence were false, and a query that genuinely broke read as a deliberate
        // deferral that left TRIM silently unchecked.
        if (context.IsElevated)
        {
            notes.Add("The TRIM setting could not be read on this system" +
                      (trim.TimedOut ? " (the query timed out)" : "") +
                      ", although this scan has administrator rights, so whether TRIM is on or off " +
                      "was NOT checked.");
            return 1;
        }

        notes.Add("TRIM query unavailable without admin on this system; the elevated half of the " +
                  "scan reads it when one runs.");
        return 0;
    }

    private static void AddFreeSpaceMetric(List<Metric> metrics, string subject, ulong size, ulong free, ushort? externalBus) =>
        metrics.Add(VolumeReadings.FreeSpace(subject, size, free, externalBus));

    [GeneratedRegex(@"DisableDeleteNotify\s*=?\s*(\d)", RegexOptions.IgnoreCase)]
    private static partial Regex TrimStateRegex();
}
