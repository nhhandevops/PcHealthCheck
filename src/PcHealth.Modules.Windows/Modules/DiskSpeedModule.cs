using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;
using PcHealth.Modules.Windows.Benchmarks;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// How fast each drive actually reads, measured rather than looked up.
///
/// The only check in this app that deliberately writes to the user's drives, and the write
/// is the smallest one that makes the measurement honest: a test file large enough that the
/// drive cannot answer from cache, created by DiskSpd, read back, and deleted when the
/// module finishes — including when it is cancelled or fails.
///
/// Absolute speed is graded loosely, against the bottom of the drive's class, because a
/// drive being slower than its marketing is not a health problem. The reading that matters
/// is the one taken again next month: a drive that has lost a third of its speed since the
/// last scan is telling you something a single number never could.
/// </summary>
internal sealed class DiskSpeedModule : WindowsModuleBase
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private const string WorkingFolder = "PcHealthCheck-bench";
    private const string TestFileName = "bench.tmp";

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "bench.disk",
        Name = "Disk speed (read benchmark)",
        Description = "Measures how fast each drive reads, in long transfers and in small scattered reads, using " +
                      "Microsoft's DiskSpd. About a minute per physical drive, so several minutes on a PC with " +
                      "a few of them. Records the result so a future slowdown becomes visible. " +
                      "Needs DiskSpd installed — the app offers to install it.",
        Reads = ["Drive model, media type and bus type (WMI)", "A temporary file this check creates itself — no existing file is opened"],
        NeedsAdmin = false,
        Writes = new WriteSpec(
            @"<drive>:\PcHealthCheck-bench\bench.tmp on each drive tested",
            DiskSpdRunner.DefaultFileBytes,
            "deleted as soon as the check finishes, including on cancel or failure"),
        EstimatedDuration = TimeSpan.FromMinutes(3),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var exe = DiskSpdRunner.Find();
        if (exe is null)
        {
            notes.Add("DiskSpd is not installed, so no speeds were measured. It is a small free tool from " +
                      "Microsoft (MIT licence); the app offers to install it with 'winget install Microsoft.DiskSpd' " +
                      "and to remove it again when you close the app.");
            return ModuleStatus.Skipped;
        }

        var (targets, skipped) = BenchmarkTargetSelector.Choose(
            ReadVolumes(notes), ReadDisks(notes), DiskSpdRunner.DefaultFileBytes);
        foreach (var reason in skipped) notes.Add("Not benchmarked — " + reason);

        if (targets.Count == 0)
        {
            notes.Add("No drive had enough free space to be benchmarked safely.");
            return ModuleStatus.Skipped;
        }

        var runner = new DiskSpdRunner(context.Processes);
        var failures = 0;

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ModuleProgress($"Measuring {target.DriveLetter} ({target.Model})…"));
            failures += await MeasureTargetAsync(runner, exe, target, context, metrics, notes, ct).ConfigureAwait(false);
        }

        return failures == 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted;
    }

    private async Task<int> MeasureTargetAsync(
        DiskSpdRunner runner, string exe, BenchmarkTarget target, ModuleContext context,
        List<Metric> metrics, List<string> notes, CancellationToken ct)
    {
        var folder = Path.Combine(target.DriveLetter + @"\", WorkingFolder);
        var file = Path.Combine(folder, TestFileName);
        var subject = $"{target.DriveLetter} ({target.Model})";

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notes.Add($"{subject}: could not create a test file here ({e.Message}), so it was not measured.");
            return 1;
        }

        try
        {
            // The first pass creates the file; the second reuses it, so the drive is only
            // written to once no matter how many profiles are measured.
            var (sequential, seqError) = await runner
                .MeasureAsync(exe, DiskProfile.Sequential, file, DiskSpdRunner.DefaultFileBytes, ct)
                .ConfigureAwait(false);
            context.Receipt.Add(new FileWriteReceipt(DateTimeOffset.Now, Manifest.Id, file,
                DiskSpdRunner.DefaultFileBytes, "benchmark test file", Deleted: false));

            if (sequential is null)
            {
                notes.Add($"{subject}: {seqError}");
                return 1;
            }

            var (random, rndError) = await runner
                .MeasureAsync(exe, DiskProfile.Random4K, file, createBytes: null, ct)
                .ConfigureAwait(false);
            if (random is null) notes.Add($"{subject}: the small-reads test did not complete — {rndError}");

            AddMetrics(target, subject, sequential, random, metrics);
            return random is null ? 1 : 0;
        }
        finally
        {
            CleanUp(file, folder, context, notes);
        }
    }

    private void AddMetrics(
        BenchmarkTarget target, string subject, DiskSpdReading sequential, DiskSpdReading? random, List<Metric> metrics)
    {
        var expectation = DriveClassifier.Expectations(target.Class);
        var classLabel = DescribeClass(target.Class);

        metrics.Add(new Metric("bench.disk.seq_read_mbps", "Sequential read speed",
            Math.Round(sequential.ReadMegabytesPerSecond, 1), HealthCategory.Performance,
            Unit: "MB/s", Subject: subject, InfoOnly: true,
            Detail: $"Measured over {sequential.TestSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds on " +
                    $"a {classLabel}. Typical: {expectation.TypicalRange}. {expectation.Note}"));

        if (expectation.SequentialFloorMbps is { } floor)
        {
            var percent = sequential.ReadMegabytesPerSecond / floor * 100;
            metrics.Add(new Metric("bench.disk.seq_read_pct_of_class", "Sequential speed against its class",
                Math.Round(percent, 0), HealthCategory.Performance, Unit: "%", Subject: subject,
                Detail: $"100% means this drive reaches the slowest speed expected of a {classLabel} " +
                        $"({floor.ToString("0", CultureInfo.InvariantCulture)} MB/s). Above 100% is normal and good; " +
                        "well below it means something is holding the drive back."));
        }

        if (random is not null)
        {
            metrics.Add(new Metric("bench.disk.rnd4k_read_mbps", "Small scattered reads (4 KB)",
                Math.Round(random.ReadMegabytesPerSecond, 2), HealthCategory.Performance,
                Unit: "MB/s", Subject: subject, InfoOnly: true,
                Detail: $"{random.ReadIops.ToString("N0", CultureInfo.InvariantCulture)} reads a second, averaging " +
                        $"{random.AverageLatencyMs.ToString("0.00", CultureInfo.InvariantCulture)} ms each. This is " +
                        "the number that decides how quick a PC feels — starting apps, opening menus, searching. " +
                        "It is reported without a verdict because a fair threshold depends on the exact drive model."));
        }
    }

    /// <summary>
    /// The promise on the consent row, kept. Runs on every path out of the measurement,
    /// and the receipt is updated so the report shows the file as deleted rather than
    /// leaving the user to wonder.
    /// </summary>
    private void CleanUp(string file, string folder, ModuleContext context, List<string> notes)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
            context.Receipt.MarkFileDeleted(file);
            if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notes.Add($"The benchmark file {file} could not be deleted ({e.Message}). " +
                      "It is safe to delete by hand — it contains no data of yours.");
        }
    }

    private static List<VolumeInfo> ReadVolumes(List<string> notes)
    {
        var partitions = Wmi.TryQuery(StorageNamespace,
            "SELECT DriveLetter, DiskNumber FROM MSFT_Partition", out var partitionError);
        var volumes = Wmi.TryQuery(StorageNamespace,
            "SELECT DriveLetter, DriveType, SizeRemaining FROM MSFT_Volume", out var volumeError);

        if (partitionError is not null || volumeError is not null)
        {
            notes.Add($"The list of drives could not be read: {partitionError ?? volumeError}");
            return [];
        }

        var diskByLetter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in partitions)
        {
            var letter = NormalizeLetter(Wmi.GetString(row, "DriveLetter"));
            if (letter is null || Wmi.Get<uint>(row, "DiskNumber") is not { } number) continue;
            diskByLetter[letter] = (int)number;
        }

        var result = new List<VolumeInfo>();
        foreach (var row in volumes)
        {
            var letter = NormalizeLetter(Wmi.GetString(row, "DriveLetter"));
            if (letter is null || Wmi.Get<uint>(row, "DriveType") is not 3) continue;
            if (!diskByLetter.TryGetValue(letter, out var diskNumber)) continue;
            result.Add(new VolumeInfo(letter, diskNumber, (long)(Wmi.Get<ulong>(row, "SizeRemaining") ?? 0)));
        }
        return result;
    }

    private static List<PhysicalDiskInfo> ReadDisks(List<string> notes)
    {
        var rows = Wmi.TryQuery(StorageNamespace,
            "SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk", out var error);
        if (error is not null)
        {
            notes.Add($"Drive models could not be read: {error}");
            return [];
        }

        var disks = new List<PhysicalDiskInfo>();
        foreach (var row in rows)
        {
            if (!int.TryParse(Wmi.GetString(row, "DeviceId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }
            disks.Add(new PhysicalDiskInfo(
                number,
                Wmi.GetString(row, "FriendlyName")?.Trim() ?? "unnamed drive",
                DriveClassifier.Classify(Wmi.Get<ushort>(row, "MediaType"), Wmi.Get<ushort>(row, "BusType"))));
        }
        return disks;
    }

    /// <summary>WMI hands back "C" for a lettered volume and a blank or NUL for one with no
    /// letter; both shapes have to end up as "C:" or nothing.</summary>
    private static string? NormalizeLetter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var letter = raw.Trim().TrimEnd(':', '\\', '\0');
        return letter.Length == 1 && char.IsLetter(letter[0]) ? letter.ToUpperInvariant() + ":" : null;
    }

    private static string DescribeClass(DriveClass driveClass) => driveClass switch
    {
        DriveClass.Hdd => "hard disk",
        DriveClass.SataSsd => "SATA SSD",
        DriveClass.NvmeSsd => "NVMe SSD",
        DriveClass.External => "drive in an external enclosure",
        _ => "drive of unknown type",
    };
}
