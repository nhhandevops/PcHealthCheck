using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Benchmarks;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// How much work this processor actually gets through, using 7-Zip's benchmark.
///
/// Reported without a verdict, and that is deliberate. A rating in MIPS is meaningful only
/// against the same processor: an office laptop and a gaming desktop are both perfectly
/// healthy at wildly different numbers, and grading one against the other would be inventing
/// a problem. What this check is really for is the comparison with itself — a processor that
/// scores a third lower than it always has is throttling, stuck on a power-saving plan, or
/// sitting under a cooler that needs attention.
///
/// Writes nothing: 7-Zip's benchmark runs entirely in memory.
/// </summary>
internal sealed class CpuBenchmarkModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "bench.cpu",
        Name = "Processor speed (benchmark)",
        Description = "Loads every processor core for about a minute using 7-Zip's benchmark and records the score, " +
                      "so a future slowdown from overheating or power settings becomes visible. Writes nothing. " +
                      "Needs 7-Zip installed — the app offers to install it.",
        Reads = ["Processor name and core count (WMI)", "Nothing on disk — the benchmark runs in memory"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(70),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var exe = SevenZipBenchmark.Find();
        if (exe is null)
        {
            notes.Add("7-Zip is not installed, so the processor was not measured. The app offers to install it " +
                      "with 'winget install 7zip.7zip' and to remove it again when you close the app. " +
                      "If you would rather not install anything, unpacking 7-Zip's standalone 7za.exe anywhere " +
                      "on your PATH works just as well.");
            return ModuleStatus.Skipped;
        }

        progress?.Report(new ModuleProgress(
            "Loading every processor core for about a minute — the PC will feel busy and the fans may speed up."));

        var (reading, error) = await new SevenZipBenchmark(context.Processes).MeasureAsync(exe, ct).ConfigureAwait(false);
        if (reading is null)
        {
            notes.Add($"The processor benchmark did not complete: {error}");
            return ModuleStatus.PartiallyCompleted;
        }

        var subject = DescribeProcessor(notes);

        metrics.Add(new Metric("bench.cpu.rating_mips", "Processor benchmark score",
            Math.Round(reading.OverallRating, 0), HealthCategory.Performance,
            Unit: "MIPS", Subject: subject, InfoOnly: true,
            Detail: "7-Zip's own rating with every core loaded. There is no good or bad number here — a quiet " +
                    "office laptop and a gaming desktop are both healthy at very different scores. What matters " +
                    "is this number staying steady: run this check again in a month, and a score well below " +
                    "today's usually means the processor is overheating and slowing itself down, or a power " +
                    "plan has changed."));

        if (reading.CompressRating > 0 && reading.DecompressRating > 0)
        {
            metrics.Add(new Metric("bench.cpu.compress_mips", "Compression score", Math.Round(reading.CompressRating, 0),
                HealthCategory.Performance, Unit: "MIPS", Subject: subject, InfoOnly: true,
                Detail: $"Decompression scored {reading.DecompressRating.ToString("N0", CultureInfo.InvariantCulture)} MIPS. " +
                        "Compression leans on memory speed as well as the processor, so the two moving apart over " +
                        "time can point at memory rather than the chip."));
        }

        return ModuleStatus.Completed;
    }

    private static string DescribeProcessor(List<string> notes)
    {
        var row = Wmi.TryQuery(@"root\cimv2",
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor", out var error).FirstOrDefault();
        if (row is null)
        {
            if (error is not null) notes.Add($"The processor name could not be read: {error}");
            return "this processor";
        }

        var name = Wmi.GetString(row, "Name")?.Trim() ?? "unknown processor";
        var cores = Wmi.Get<uint>(row, "NumberOfCores");
        var threads = Wmi.Get<uint>(row, "NumberOfLogicalProcessors");

        // The subject is what the trend history keys on, so it must stay stable between
        // scans — hence the processor's own name rather than anything that could vary.
        return cores is { } c && threads is { } t
            ? $"{name} ({c.ToString(CultureInfo.InvariantCulture)} cores / {t.ToString(CultureInfo.InvariantCulture)} threads)"
            : name;
    }
}
