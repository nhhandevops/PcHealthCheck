using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Linux.Support;

namespace PcHealth.Modules.Linux.Modules;

/// <summary>Free space on each local disk filesystem. Network filesystems are never asked.</summary>
internal sealed class LinuxVolumesModule(SystemFiles files, Func<string, VolumeSpace> statfs) : HealthModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "linux.volumes",
        Name = "Volumes & free space",
        Description = "Free space on each local disk filesystem. Network shares are never touched: asking one for its free space would make a network connection.",
        Reads =
        [
            "Mounted filesystems (/proc/self/mounts)",
            "Size and free space of local disk filesystems (statvfs)",
            "Whether a disk is removable (/sys/class/block)",
        ],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(1),
    };

    private const double BytesPerGb = 1073741824.0;

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        var text = files.TryRead("/proc/self/mounts", out var error) ?? files.TryRead("/proc/mounts");
        if (text is null)
        {
            failures.Record("Mounted filesystems", error ?? "unreadable");
            return Task.FromResult(failures.Resolve(gotAnything: false));
        }

        var mounts = MountTable.Parse(text);
        var volumes = MountTable.LocalVolumes(mounts, IsRemovable);
        if (volumes.Count == 0)
        {
            failures.Record("Free space", $"none of the {mounts.Count} mounted filesystems is a local disk " +
                                          "(inside a container the root is usually overlay, which is not checked)");
            return Task.FromResult(failures.Resolve(gotAnything: false));
        }

        foreach (var volume in volumes)
        {
            ct.ThrowIfCancellationRequested();
            VolumeSpace space;
            try
            {
                space = statfs(volume.MountPoint);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Record($"Free space on {volume.MountPoint}", e.Message);
                continue;
            }
            if (MountTable.FreePct(space) is not { } freePct) continue;

            var also = volume.AlsoMountedAt.Length == 0 ? "" : $" Also mounted at {string.Join(", ", volume.AlsoMountedAt)}.";
            metrics.Add(new Metric("storage.volume.free_pct", "Free space", freePct, HealthCategory.Storage,
                Unit: "%", Subject: volume.MountPoint,
                Detail: $"{space.AvailableBytes / BytesPerGb:0.0} GB free of {space.TotalBytes / BytesPerGb:0} GB " +
                        $"({volume.FsType} on {volume.Device}), counted like df: space only root may use is not free to you.{also} " +
                        "SSDs slow down and wear faster when nearly full."));
        }

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    /// <summary>
    /// Whether a device is a removable medium such as a USB stick, which the Windows check also
    /// leaves out. A partition (sdb1) has no "removable" of its own; its disk (sdb) does, and is
    /// found through the partition's link in /sys/class/block. Unknown counts as fixed, so a disk
    /// is never dropped from the report for lack of an answer.
    /// </summary>
    private bool IsRemovable(string device)
    {
        if (!device.StartsWith("/dev/", StringComparison.Ordinal)) return false;
        var name = Path.GetFileName(device);
        var flag = files.TryRead($"/sys/class/block/{name}/removable")
                   ?? (files.LinkTargetParentName($"/sys/class/block/{name}") is { } disk
                       ? files.TryRead($"/sys/class/block/{disk}/removable")
                       : null);
        return ProcFiles.ParseInt(flag) == 1;
    }
}
