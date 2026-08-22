using System.Text.RegularExpressions;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>Free space and health status per fixed volume, plus TRIM state for SSDs.</summary>
internal sealed partial class VolumesModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "volumes",
        Name = "Volumes & free space",
        Description = "Free space and reported health per fixed volume; whether TRIM is enabled for SSDs.",
        Reads = ["Volume list, sizes and health (WMI)", "TRIM setting (fsutil, read-only query)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(3),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = 0;

        var volumes = Wmi.TryQuery(@"root\Microsoft\Windows\Storage",
            "SELECT DriveLetter, FileSystemLabel, DriveType, HealthStatus, Size, SizeRemaining FROM MSFT_Volume",
            out var msftError);

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
                    AddFreeSpaceMetric(metrics, subject, size.Value, remaining.Value);
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
                if (size is > 0 && free is not null) AddFreeSpaceMetric(metrics, subject, size.Value, free.Value);
                if (ldError is not null) { notes.Add($"LogicalDisk query: {ldError}"); failures++; break; }
            }
        }

        // TRIM state — read-only fsutil query; on many systems it needs admin, so treat failure as a note.
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
            }
            else
            {
                // A parse that fails on a non-English Windows is exactly the locale trap this
                // codebase already has analyzers for; it must not pass as "TRIM is fine".
                notes.Add("Could not interpret the TRIM setting from fsutil on this system's language, so TRIM " +
                          "was NOT checked. It is not known whether it is on or off.");
                failures++;
            }
        }
        else
        {
            notes.Add("TRIM query unavailable without admin on this system; it is covered by the elevated scan.");
        }

        if (metrics.Count == 0) return ModuleStatus.Failed;
        return failures == 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted;
    }

    private static void AddFreeSpaceMetric(List<Metric> metrics, string subject, ulong size, ulong free)
    {
        var freePct = Math.Round(free * 100.0 / size, 1);
        var freeGb = Math.Round(free / 1073741824.0, 1);
        var sizeGb = Math.Round(size / 1073741824.0, 0);
        metrics.Add(new Metric("storage.volume.free_pct", "Free space", freePct, HealthCategory.Storage,
            Unit: "%", Subject: subject,
            Detail: $"{freeGb} GB free of {sizeGb} GB. SSDs slow down and wear faster when nearly full."));
    }

    [GeneratedRegex(@"DisableDeleteNotify\s*=?\s*(\d)", RegexOptions.IgnoreCase)]
    private static partial Regex TrimStateRegex();
}
