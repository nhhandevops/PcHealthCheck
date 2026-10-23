using System.Text;

namespace PcHealth.Modules.Linux.Support;

/// <summary>One line of /proc/self/mounts, with the kernel's octal escapes undone.</summary>
internal sealed record MountEntry(string Device, string MountPoint, string FsType, string[] Options);

/// <summary>A local disk filesystem worth checking, and the other places the same one is mounted.</summary>
internal sealed record LocalVolume(string Device, string MountPoint, string FsType, string[] AlsoMountedAt);

/// <summary>What statvfs says about one filesystem, in bytes.</summary>
internal sealed record VolumeSpace(long TotalBytes, long FreeBytes, long AvailableBytes);

/// <summary>
/// Which mounted filesystems the free-space check looks at. A typical Linux machine has dozens
/// of mounts and only two or three of them are disks: the rest are kernel filesystems (proc,
/// sysfs, cgroup), memory (tmpfs), snap packages (squashfs, always 100% full by design) and
/// container layers (overlay). Grading those would bury the real result in noise.
/// </summary>
internal static class MountTable
{
    /// <summary>
    /// Filesystems that live on a local disk. An allow-list on purpose: network filesystems (nfs,
    /// cifs, sshfs, other fuse.*) are left out, because asking one for its free space makes a
    /// network connection, and a scan makes none. It also keeps the scan from hanging on a share
    /// whose server has gone away.
    /// </summary>
    public static readonly HashSet<string> DiskFileSystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "f2fs", "jfs", "reiserfs", "bcachefs", "zfs",
        "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "hfsplus",
    };

    public static List<MountEntry> Parse(string text)
    {
        var entries = new List<MountEntry>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4) continue;
            entries.Add(new MountEntry(Unescape(fields[0]), Unescape(fields[1]), fields[2], fields[3].Split(',')));
        }
        return entries;
    }

    /// <summary>
    /// The kernel writes a space in a mount point as \040, a tab as \011, a newline as \012
    /// and a backslash as \134, so a line always splits on spaces correctly.
    /// </summary>
    public static string Unescape(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal)) return field;
        var result = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && IsOctal(field, i + 1))
            {
                result.Append((char)((field[i + 1] - '0') * 64 + (field[i + 2] - '0') * 8 + (field[i + 3] - '0')));
                i += 3;
                continue;
            }
            result.Append(field[i]);
        }
        return result.ToString();
    }

    private static bool IsOctal(string s, int start) =>
        start + 3 <= s.Length && s[start] is >= '0' and <= '7' && s[start + 1] is >= '0' and <= '7' && s[start + 2] is >= '0' and <= '7';

    /// <summary>
    /// The local disk filesystems, one per device, in mount order. A device mounted twice (btrfs
    /// subvolumes for / and /home, or a bind mount) has one free-space figure, so it is reported
    /// once, under the first place it is mounted. Read-only mounts, loop-mounted images and
    /// removable media are left out: their free space is not something to act on.
    /// </summary>
    public static List<LocalVolume> LocalVolumes(IEnumerable<MountEntry> entries, Func<string, bool>? isRemovable = null)
    {
        var byDevice = new Dictionary<string, (MountEntry First, List<string> Others)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var entry in entries)
        {
            if (!DiskFileSystems.Contains(entry.FsType)) continue;
            if (entry.Options.Contains("ro", StringComparer.Ordinal)) continue;
            if (entry.Device.StartsWith("/dev/loop", StringComparison.Ordinal)) continue;
            if (isRemovable?.Invoke(entry.Device) == true) continue;

            // Every ZFS dataset reports the free space of its pool, so the pool is the device.
            var key = entry.FsType == "zfs" ? entry.Device.Split('/')[0] : entry.Device;
            if (byDevice.TryGetValue(key, out var seen))
            {
                seen.Others.Add(entry.MountPoint);
                continue;
            }
            byDevice[key] = (entry, []);
            order.Add(key);
        }
        return [.. order.Select(key =>
        {
            var (first, others) = byDevice[key];
            return new LocalVolume(first.Device, first.MountPoint, first.FsType, [.. others]);
        })];
    }

    /// <summary>
    /// Free space as a percentage, counted the way df counts it: blocks only root may use are
    /// neither free to the reader nor part of the size they can fill. On ext4 that reserve is 5%
    /// by default, so a disk df calls full is not reported as 5% free.
    /// </summary>
    public static double? FreePct(VolumeSpace space)
    {
        var used = space.TotalBytes - space.FreeBytes;
        var usable = used + space.AvailableBytes;
        return usable > 0 && used >= 0 ? Math.Round(space.AvailableBytes * 100.0 / usable, 1) : null;
    }
}
