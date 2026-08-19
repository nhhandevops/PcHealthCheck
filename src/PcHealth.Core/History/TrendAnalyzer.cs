using System.Globalization;
using PcHealth.Core.Metrics;

namespace PcHealth.Core.History;

public enum TrendKind
{
    /// <summary>A counter that should never move has moved.</summary>
    CounterGrowth,

    /// <summary>A wear/capacity figure is heading somewhere, and we can say roughly when it arrives.</summary>
    Projection,
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
    IReadOnlyList<string> Tips);

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
            [
                "BACK UP THIS DRIVE NOW. A counter that is still climbing means the drive is failing while you read this.",
                "Copy anything irreplaceable off the drive before further troubleshooting, and verify the copy opens.",
                metric.Id == "smart.crc_errors"
                    ? "For interface errors specifically: replace the drive's data cable first — that fixes most of them."
                    : "Plan to replace the drive. Do not keep the only copy of anything on it.",
            ]);
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
            grade, message, oldest.Value, current, oldest.TakenAt, tips);
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
