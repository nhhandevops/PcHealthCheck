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
        Description = "Measures how much work one processor core gets through, using 7-Zip's benchmark, and " +
                      "records it so a future slowdown from overheating or power settings becomes visible. " +
                      "About 30 seconds, writes nothing, and leaves the rest of the PC usable. " +
                      "Needs 7-Zip installed — the app offers to install it.",
        Reads = ["Processor name and core count (WMI)", "Nothing on disk — the benchmark runs in memory"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(35),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        var exe = SevenZipBenchmark.Find();
        if (exe is null)
        {
            // Choosing not to install a benchmark tool is not a broken check, so this stays a
            // plain note: if it degraded the scan, every PC without 7-Zip would raise the
            // "some checks could not finish" banner and the banner would stop meaning anything.
            failures.Note("7-Zip is not installed, so the processor was not measured. The app offers to install it " +
                          "with 'winget install 7zip.7zip' and to remove it again when you close the app. " +
                          "If you would rather not install anything, unpacking 7-Zip's standalone 7za.exe anywhere " +
                          "on your PATH works just as well.");
            return ModuleStatus.Skipped;
        }

        progress?.Report(new ModuleProgress("Measuring one processor core — this takes about 30 seconds."));

        var (reading, error) = await new SevenZipBenchmark(context.Processes).MeasureAsync(exe, ct).ConfigureAwait(false);
        if (reading is null)
        {
            failures.Record("The processor benchmark did not complete", error ?? "7-Zip gave no reason.");
            return failures.Resolve(gotAnything: metrics.Count > 0);
        }

        var subject = DescribeProcessor(failures);

        metrics.Add(new Metric("bench.cpu.single_thread_mips", "Processor benchmark score",
            Math.Round(reading.OverallRating, 0), HealthCategory.Performance,
            Unit: "MIPS", Subject: subject, InfoOnly: true,
            Detail: "7-Zip's rating for a single core. There is no good or bad number here — a quiet office " +
                    "laptop and a gaming desktop are both healthy at very different scores. What matters is this " +
                    "number staying steady: run the check again in a month, and a score well below today's " +
                    "usually means the processor is slowing itself down to stay cool, or a power plan has " +
                    "changed. One core rather than all of them on purpose — loading every core for a minute " +
                    "heats the chip enough that the test starts measuring its own effect, which was measured at " +
                    "a 45% swing between back-to-back runs."));

        if (reading.CompressRating > 0 && reading.DecompressRating > 0)
        {
            metrics.Add(new Metric("bench.cpu.compress_mips", "Compression score", Math.Round(reading.CompressRating, 0),
                HealthCategory.Performance, Unit: "MIPS", Subject: subject, InfoOnly: true,
                Detail: $"Decompression scored {reading.DecompressRating.ToString("N0", CultureInfo.InvariantCulture)} MIPS. " +
                        "Compression leans on memory speed as well as the processor, so the two moving apart over " +
                        "time can point at memory rather than the chip."));
        }
        else
        {
            // SevenZipOutputReader zeroes both figures when the "Avr:" row is not the eight
            // columns it expects, rather than reading a rating out of the wrong column.
            // Suppressing the metric is right; reporting the scan as clean afterwards is not —
            // a parser that refused to guess must not read back as a processor that was
            // measured in full, and a graded 0 MIPS would be the same lie the other way round.
            failures.Record("Compression and decompression scores",
                "7-Zip's benchmark table was not the shape this app knows how to read, so those two figures " +
                "were NOT measured. The overall score above comes from a different row and is unaffected.");
        }

        return failures.Resolve(gotAnything: metrics.Count > 0);
    }

    private static string DescribeProcessor(ModuleFailures failures)
    {
        // A name that could not be read is not cosmetic: the subject is what the trend history
        // keys on, so falling back to "this processor" starts a second, empty history and the
        // run-it-again-in-a-month comparison this check exists for never happens.
        var row = Wmi.TryQuery(@"root\cimv2",
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor",
            failures, "The processor name could not be read").FirstOrDefault();
        if (row is null) return "this processor";

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
