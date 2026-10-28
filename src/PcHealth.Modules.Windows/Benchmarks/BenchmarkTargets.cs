namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>A volume standing in for the physical drive underneath it.</summary>
public sealed record BenchmarkTarget(
    string DriveLetter,
    int DiskNumber,
    string Model,
    DriveClass Class,
    long FreeBytes);

/// <summary>One volume as Windows reports it, before any decision has been made about it.</summary>
public sealed record VolumeInfo(string DriveLetter, int DiskNumber, long FreeBytes);

/// <summary>One physical drive as Windows reports it.</summary>
public sealed record PhysicalDiskInfo(int DiskNumber, string Model, DriveClass Class)
{
    /// <summary>
    /// MSFT_PhysicalDisk.HealthStatus: 0 Healthy, 1 Warning, 2 Unhealthy. Null when Windows
    /// did not say, which is treated as healthy — refusing to measure every drive whose
    /// health is merely unknown would disable the check on most external hardware.
    /// </summary>
    public ushort? HealthStatus { get; init; }

    public bool WindowsCallsItUnhealthy => HealthStatus is 1 or 2;
}

/// <summary>
/// Decides which drives to benchmark.
///
/// Two rules do all the work. **One test per physical drive**, because C: and E: on the same
/// SSD would produce two nearly identical numbers, take twice as long, and write twice as
/// much for no extra information. And **only where there is comfortable room**, because
/// filling a drive to run a benchmark on it is the opposite of looking after someone's data.
/// </summary>
public static class BenchmarkTargetSelector
{
    /// <summary>
    /// Headroom kept free beyond the test file itself. A drive squeezed to its last byte
    /// slows down, wears faster, and can break Windows Update — this test must never be
    /// the thing that puts it there.
    /// </summary>
    public const double RequiredHeadroom = 1.15;

    /// <summary>
    /// <paramref name="failing"/> is the scan's own verdict on the drives, by disk number, with the
    /// reason (<see cref="Support.FailingDrives"/>). A drive in it is never written to.
    /// </summary>
    public static (IReadOnlyList<BenchmarkTarget> Targets, IReadOnlyList<string> Skipped) Choose(
        IEnumerable<VolumeInfo> volumes,
        IEnumerable<PhysicalDiskInfo> disks,
        long fileBytes,
        IReadOnlyDictionary<int, string>? failing = null)
    {
        var byNumber = disks.GroupBy(d => d.DiskNumber).ToDictionary(g => g.Key, g => g.First());
        var needed = (long)(fileBytes * RequiredHeadroom);
        var targets = new List<BenchmarkTarget>();
        var skipped = new List<string>();

        foreach (var group in volumes.GroupBy(v => v.DiskNumber).OrderBy(g => g.Key))
        {
            if (!byNumber.TryGetValue(group.Key, out var disk))
            {
                skipped.Add($"{Describe(group)}: Windows did not report which physical drive this is.");
                continue;
            }

            // A drive Windows already reports as failing is the one drive this must not
            // touch. Everything else in this app tells the user to back such a drive up and
            // stop using it; writing a gigabyte to it to see how fast it still is would be
            // the app contradicting its own advice, and could finish the drive off.
            if (disk.WindowsCallsItUnhealthy)
            {
                skipped.Add($"{Describe(group)} ({disk.Model}): Windows reports this drive as failing. " +
                            "Benchmarking writes to it, and a drive in that state should be backed up and left " +
                            "alone, not measured.");
                continue;
            }

            // Windows' flag said "Healthy" on the pilot's SSD while SMART counted 5 retired
            // sectors, so the app's own findings decide too (REMEDIES-PLAN H2).
            if (failing is not null && failing.TryGetValue(group.Key, out var reason))
            {
                skipped.Add($"{Describe(group)} ({disk.Model}): {reason}. Measuring writes a large test file to " +
                            "the drive, and a drive in that state should be backed up and left alone, not measured.");
                continue;
            }

            // The roomiest volume on the drive: same hardware either way, least intrusive.
            var best = group.OrderByDescending(v => v.FreeBytes).First();
            if (best.FreeBytes < needed)
            {
                skipped.Add($"{best.DriveLetter} ({disk.Model}): needs {Gigabytes(needed)} GB free to test safely, " +
                            $"has {Gigabytes(best.FreeBytes)} GB.");
                continue;
            }

            targets.Add(new BenchmarkTarget(best.DriveLetter, group.Key, disk.Model, disk.Class, best.FreeBytes));
        }

        return (targets, skipped);
    }

    private static string Describe(IEnumerable<VolumeInfo> group) =>
        string.Join(", ", group.Select(v => v.DriveLetter));

    private static string Gigabytes(long bytes) =>
        (bytes / 1024.0 / 1024 / 1024).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
