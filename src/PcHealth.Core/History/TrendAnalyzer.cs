using System.Globalization;
using PcHealth.Core.Metrics;

namespace PcHealth.Core.History;

public enum TrendKind
{
    /// <summary>A counter that should never move has moved.</summary>
    CounterGrowth,

    /// <summary>A wear/capacity figure is heading somewhere, and we can say roughly when it arrives.</summary>
    Projection,

    /// <summary>A benchmark has come back materially slower than this machine's own history.</summary>
    PerformanceDrop,
}

public sealed record TrendFinding(
    TrendKind Kind,
    string MetricId,
    string? Subject,
    string Label,
    Grade Grade,
    string Message,
    double PreviousValue,
    double CurrentValue,
    DateTimeOffset PreviousSeen,
    IReadOnlyList<string> Tips,
    /// <summary>The category the underlying measurement belongs to, so a battery trend
    /// affects the battery score rather than the storage score.</summary>
    HealthCategory Category = HealthCategory.Storage);

/// <summary>
/// Compares this scan against what earlier scans recorded. A snapshot cannot tell the
/// difference between a drive that has had 2 bad sectors for two years and one that grew
/// 2 bad sectors this week; the second is an emergency and this is what spots it.
/// </summary>
public static class TrendAnalyzer
{
    /// <summary>
    /// Counters where any increase is a warning and a jump from zero is an alarm.
    /// Keyed by metric id, valued by how the finding is worded.
    /// </summary>
    private static readonly Dictionary<string, string> GrowthWatchList = new(StringComparer.Ordinal)
    {
        ["smart.reallocated"] = "sectors have been retired",
        ["smart.pending_sectors"] = "sectors have become unstable",
        ["smart.reported_uncorrectable"] = "uncorrectable errors have been reported",
        ["smart.offline_uncorrectable"] = "sectors failed the drive's background scan",
        ["smart.command_timeout"] = "commands have timed out",
        ["smart.crc_errors"] = "interface (cable) errors have occurred",
        ["storage.read_errors_uncorrected"] = "reads have failed uncorrectably",
        ["storage.write_errors_uncorrected"] = "writes have failed uncorrectably",
    };

    /// <summary>Wear-style metrics that climb toward a limit we can extrapolate to.</summary>
    private static readonly Dictionary<string, (double Limit, string What)> RisingToLimit = new(StringComparer.Ordinal)
    {
        ["storage.wear_pct"] = (100, "rated write endurance"),
        ["smart.ssd_percent_consumed"] = (100, "rated write endurance"),
    };

    /// <summary>
    /// Benchmarks judged against the machine's own past rather than any absolute number.
    /// This is the whole point of measuring speed: "2,100 MB/s" means little on its own,
    /// but "a third slower than this drive has always been" means something on any drive.
    /// </summary>
    private static readonly Dictionary<string, PerformanceWatch> PerformanceWatchList = new(StringComparer.Ordinal)
    {
        ["bench.disk.seq_read_mbps"] = new("reading long files", DiskAdvice),
        // Scattered reads are the noisiest thing measured here. Back to back on an idle PC
        // they vary about 11%, but across whole scans — where the drive has just been worked
        // by other checks — a 26% swing was seen between two healthy runs. Against the
        // ordinary 30% rule that leaves almost no margin, and a threshold a healthy machine
        // trips is a bug in the threshold rather than a finding.
        ["bench.disk.rnd4k_read_mbps"] = new("reading small scattered files", DiskAdvice, NoticeableDrop: 0.55),
        ["bench.cpu.single_thread_mips"] = new("processing work", CpuAdvice),
    };

    /// <summary>
    /// What a slowdown in this measurement usually means, what to do about it, and how far
    /// it has to fall before it means anything at all. The last part is per-measurement
    /// because their natural variance differs by an order of magnitude: sequential reads
    /// repeat to within 1%, scattered reads to within 11-26%.
    /// </summary>
    private sealed record PerformanceWatch(
        string Activity,
        Func<Grade, IReadOnlyList<string>> Advice,
        double NoticeableDrop = TrendAnalyzer.NoticeableDrop,
        double SevereDrop = TrendAnalyzer.SevereDrop);

    private static IReadOnlyList<string> DiskAdvice(Grade grade) =>
    [
        "Re-run the check on an idle PC before anything else — a backup, a game update or an antivirus scan running at the same time explains most sudden drops.",
        "Check free space: a drive under about 10% free slows down noticeably, and an SSD more than a hard disk.",
        grade == Grade.Bad
            ? "If it stays this slow when nothing else is running, BACK UP the drive and read the Storage section of this report — drives sometimes slow down before they fail."
            : "If it stays slightly slow, check for a loose SATA cable or, for NVMe, that the drive is still in a full-speed slot.",
    ];

    private static IReadOnlyList<string> CpuAdvice(Grade grade) =>
    [
        "Re-run the check with nothing else running — anything busy in the background takes cores away from the benchmark and lowers the score.",
        "Check the Temperatures & fans section of this report. A processor that gets too hot slows itself down to survive, and that is the most common reason a score falls over time. Dust in the cooler and dried-out thermal paste are the usual causes.",
        grade == Grade.Bad
            ? "Also check Windows' power mode (Settings > System > Power) and, on a laptop, that it is plugged in — 'Best power efficiency' or battery power can halve the score legitimately."
            : "On a laptop, a lower score on battery than on mains is normal and not a fault.",
    ];

    /// <summary>Below this share of the usual figure, say something. Anything milder is noise:
    /// a background update or an antivirus scan easily costs 10-20%. Individual measurements
    /// override it where their own variance says they must.</summary>
    internal const double NoticeableDrop = 0.70;

    /// <summary>Below this share, treat it as a real problem rather than a busy moment.</summary>
    internal const double SevereDrop = 0.50;

    /// <summary>Metrics that fall toward a limit (battery capacity heading for replacement territory).</summary>
    private static readonly Dictionary<string, (double Limit, string What)> FallingToLimit = new(StringComparer.Ordinal)
    {
        ["battery.capacity_pct"] = (60, "the point where a battery is usually replaced"),
    };

    public static IReadOnlyList<TrendFinding> Analyze(IEnumerable<Metric> currentMetrics, HistoryStore history)
    {
        var findings = new List<TrendFinding>();

        foreach (var metric in currentMetrics)
        {
            if (metric.Value.Number is not { } current) continue;

            // History written by THIS scan is excluded by callers recording after analysis;
            // defensively we still compare against samples strictly older than now.
            var series = history.GetSeries(metric.Id, metric.Subject);
            if (series.Count == 0) continue;

            if (GrowthWatchList.TryGetValue(metric.Id, out var phrasing))
            {
                var previous = series[^1];
                if (current > previous.Value)
                {
                    findings.Add(BuildGrowthFinding(metric, current, previous, phrasing));
                }
            }
            else if (RisingToLimit.TryGetValue(metric.Id, out var rising))
            {
                var finding = BuildProjection(metric, current, series, rising.Limit, rising.What, isRising: true);
                if (finding is not null) findings.Add(finding);
            }
            else if (FallingToLimit.TryGetValue(metric.Id, out var falling))
            {
                var finding = BuildProjection(metric, current, series, falling.Limit, falling.What, isRising: false);
                if (finding is not null) findings.Add(finding);
            }
            else if (PerformanceWatchList.TryGetValue(metric.Id, out var watch))
            {
                var finding = BuildPerformanceDrop(metric, current, series, watch);
                if (finding is not null) findings.Add(finding);
            }
        }

        return findings;
    }

    private static TrendFinding BuildGrowthFinding(Metric metric, double current, Sample previous, string phrasing)
    {
        var grewFromZero = previous.Value == 0;
        var since = Describe(DateTimeOffset.Now - previous.TakenAt);

        var message = grewFromZero
            ? $"This is new: {phrasing} for the first time. The last scan {since} recorded none, and there are now {Format(current)}."
            : $"This is getting worse: {Format(previous.Value)} at the last scan {since}, {Format(current)} now.";

        return new TrendFinding(
            TrendKind.CounterGrowth, metric.Id, metric.Subject, metric.Label,
            Grade.Bad, message, previous.Value, current, previous.TakenAt,
            Tips:
            [
                "BACK UP THIS DRIVE NOW. A counter that is still climbing means the drive is failing while you read this.",
                "Copy anything irreplaceable off the drive before further troubleshooting, and verify the copy opens.",
                metric.Id == "smart.crc_errors"
                    ? "For interface errors specifically: replace the drive's data cable first — that fixes most of them."
                    : "Plan to replace the drive. Do not keep the only copy of anything on it.",
            ],
            Category: metric.Category);
    }

    /// <summary>
    /// Compares against the median of every previous run, not the best or the first. The
    /// best is whatever run got luckiest; the first is whatever the machine happened to be
    /// doing that day. The median is the figure this drive actually delivers.
    /// </summary>
    private static TrendFinding? BuildPerformanceDrop(
        Metric metric, double current, IReadOnlyList<Sample> series, PerformanceWatch watch)
    {
        // One past reading is an anecdote — a single busy afternoon would raise a false
        // alarm on every scan afterwards.
        if (series.Count < 2) return null;

        var usual = Median(series.Select(s => s.Value));
        if (usual <= 0) return null;

        var share = current / usual;
        if (share >= watch.NoticeableDrop) return null;

        var slowerBy = (1 - share) * 100;
        var grade = share < watch.SevereDrop ? Grade.Bad : Grade.Normal;
        var oldest = series[0];

        var message =
            $"{slowerBy.ToString("0", CultureInfo.InvariantCulture)}% slower at {watch.Activity} than this machine's " +
            $"usual {Format(usual)} (median of {series.Count.ToString(CultureInfo.InvariantCulture)} earlier scans), " +
            $"now {Format(current)}.";

        return new TrendFinding(
            TrendKind.PerformanceDrop, metric.Id, metric.Subject, metric.Label,
            grade, message, usual, current, oldest.TakenAt, watch.Advice(grade), metric.Category);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static TrendFinding? BuildProjection(
        Metric metric, double current, IReadOnlyList<Sample> series,
        double limit, string what, bool isRising)
    {
        // Two points make the line: the oldest stored sample and the reading taken now.
        // What matters is that they are far enough apart in time to mean anything.
        var oldest = series[0];
        var span = (DateTimeOffset.Now - oldest.TakenAt).TotalDays;
        if (span < 14) return null;

        var change = current - oldest.Value;
        var perDay = change / span;

        // Only project when moving toward the limit at a measurable rate.
        if (isRising && perDay <= 0) return null;
        if (!isRising && perDay >= 0) return null;

        var remaining = limit - current;
        var daysToLimit = remaining / perDay;
        if (daysToLimit <= 0 || double.IsNaN(daysToLimit) || double.IsInfinity(daysToLimit)) return null;

        var years = daysToLimit / 365.0;
        var grade = daysToLimit switch
        {
            < 180 => Grade.Bad,
            < 730 => Grade.Normal,
            _ => Grade.Good,
        };

        var reached = DateTimeOffset.Now.AddDays(Math.Min(daysToLimit, 365 * 50));
        var rateText = Math.Abs(perDay * 365) < 1
            ? $"about {Math.Abs(perDay * 365):0.0}% a year"
            : $"about {Math.Abs(perDay * 365):0}% a year";

        var message =
            $"Measured over {span:0} days: moving {rateText}, reaching {what} around {reached:MMMM yyyy} " +
            $"({(years >= 1 ? $"~{years:0.#} years" : $"~{daysToLimit:0} days")} from now).";

        var tips = grade switch
        {
            Grade.Bad =>
            [
                "At this rate the drive reaches the end of its rated life within months. Budget for a replacement now and keep backups current.",
                "If this rate looks wrong, check what is writing so much — heavy logging, torrents, or a misplaced page file will burn endurance fast.",
            ],
            Grade.Normal => new[]
            {
                "Nothing urgent, but this is the drive to replace first when you next upgrade.",
            },
            _ => Array.Empty<string>(),
        };

        return new TrendFinding(
            TrendKind.Projection, metric.Id, metric.Subject, metric.Label,
            grade, message, oldest.Value, current, oldest.TakenAt, tips, metric.Category);
    }

    private static string Format(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Describe(TimeSpan elapsed) => elapsed.TotalDays switch
    {
        < 1 => "earlier today",
        < 2 => "yesterday",
        < 14 => $"{elapsed.TotalDays:0} days ago",
        < 60 => $"{elapsed.TotalDays / 7:0} weeks ago",
        _ => $"{elapsed.TotalDays / 30:0} months ago",
    };
}
