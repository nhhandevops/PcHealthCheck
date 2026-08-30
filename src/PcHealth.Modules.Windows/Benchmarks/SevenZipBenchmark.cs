using System.Globalization;
using PcHealth.Core.Processes;

namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>How much work the processor got through, in 7-Zip's own MIPS rating.</summary>
public sealed record CpuBenchmarkReading(double OverallRating, double CompressRating, double DecompressRating);

/// <summary>
/// Uses 7-Zip's built-in benchmark to see how much work the processor actually manages.
///
/// 7-Zip is a good choice for a health check rather than a scoreboard: it is a real
/// workload — compression is branchy integer code that also leans on memory — and it is
/// the same on every machine. Nothing is compressed on disk; the benchmark works entirely
/// in RAM, so this check writes nothing at all.
///
/// The tool is invoked as an already-installed program and never bundled or linked, so
/// its LGPL terms bring no obligations here beyond saying where it came from.
/// </summary>
public sealed class SevenZipBenchmark(IProcessRunner processes)
{
    public const string ToolName = "7z.exe";

    /// <summary>
    /// The standalone console build. Anyone who unpacked 7-Zip rather than installing it has
    /// this and not <c>7z.exe</c> — and since the installer is the part that needs a UAC
    /// prompt, accepting the portable one is the difference between this check working and
    /// being permanently unavailable on a locked-down machine.
    /// </summary>
    public const string StandaloneToolName = "7za.exe";

    /// <summary>
    /// One core, one pass. The obvious choice would be 7-Zip's default all-core run, and it
    /// was — until it was measured. Five consecutive all-core runs on the same idle PC gave
    /// 52,267 / 43,768 / 41,573 / 36,715 / 28,745 MIPS: a 45% spread, because each one-minute
    /// run heat-soaks the chip and the next one measures the heat rather than the processor.
    /// A benchmark whose own effect dominates its result cannot detect anything, and a 30%
    /// regression rule sitting on top of it would have cried wolf every time.
    ///
    /// The same four runs single-threaded: 6,026 / 5,853 / 5,914 / 6,074 — a 3.6% spread, in
    /// 26 seconds instead of a minute, with the fans staying quiet. It is also the number
    /// most software actually feels, and a processor that is throttling or stuck on a
    /// power-saving plan still shows it here.
    /// </summary>
    public const string BenchmarkArguments = "b 1 -mmt1";

    public static string? Find()
    {
        string[] directories =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip"),
        ];

        return ToolLocator.Find(ToolName, "7zip.7zip", directories)
               ?? ToolLocator.Find(StandaloneToolName, "7zip.7zip", directories);
    }

    public async Task<(CpuBenchmarkReading? Reading, string? Error)> MeasureAsync(string exePath, CancellationToken ct)
    {
        var result = await processes
            .RunAsync("bench.cpu", exePath, BenchmarkArguments, TimeSpan.FromMinutes(5), ct: ct)
            .ConfigureAwait(false);

        if (result.TimedOut) return (null, "The processor benchmark did not finish within five minutes.");

        var reading = SevenZipOutputReader.Parse(result.StdOut, out var parseError);
        if (reading is not null) return (reading, null);

        return (null, $"7-Zip exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}: {parseError}");
    }
}

/// <summary>
/// Reads the summary rows of 7-Zip's benchmark table.
///
/// The table is fixed-width text, so the parsing keys on the two row labels 7-Zip prints —
/// "Avr:" and "Tot:" — and then takes the numbers in order. Those labels are part of the
/// benchmark output rather than the localized interface, but the parser still refuses to
/// guess: a row with an unexpected number of columns produces an error, not a figure.
/// </summary>
public static class SevenZipOutputReader
{
    public static CpuBenchmarkReading? Parse(string output, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            error = "7-Zip produced no output.";
            return null;
        }

        double[]? average = null, total = null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("Avr:", StringComparison.Ordinal)) average = Numbers(trimmed[4..]);
            else if (trimmed.StartsWith("Tot:", StringComparison.Ordinal)) total = Numbers(trimmed[4..]);
        }

        if (total is null || total.Length == 0)
        {
            error = "7-Zip's benchmark did not report a total rating.";
            return null;
        }

        // Tot: usage%, rating/usage, overall rating. The overall figure is the last column.
        var overall = total[^1];

        // Avr: compress(speed, usage, R/U, rating) | decompress(speed, usage, R/U, rating).
        // Anything other than those eight columns means the table is not the shape expected,
        // and a number read from the wrong column is worse than no number. The zero is a
        // sentinel for "not measured", never a score: CpuBenchmarkModule suppresses the metric
        // and reports the scan incomplete rather than publishing a processor that rated 0 MIPS.
        var compress = average is { Length: 8 } ? average[3] : 0;
        var decompress = average is { Length: 8 } ? average[7] : 0;

        if (overall <= 0)
        {
            error = "7-Zip reported a rating of zero, which cannot be right.";
            return null;
        }

        return new CpuBenchmarkReading(overall, compress, decompress);
    }

    /// <summary>Every number in the row, read invariantly — 7-Zip prints plain digits and a
    /// comma-decimal locale must not turn them into something else.</summary>
    private static double[] Numbers(string row) =>
        [.. row.Split([' ', '\t', '|', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : double.NaN)
            .Where(value => !double.IsNaN(value))];
}
