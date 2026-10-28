using System.Globalization;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Security;

/// <summary>A threat Defender recorded: what it was, and whether it is still live.</summary>
public sealed record ThreatRecord(string ThreatId, string Name, bool IsActive);

/// <summary>One detection event for a threat, carrying the outcome status.</summary>
public sealed record DetectionRecord(string ThreatId, uint StatusId, DateTime? DetectedAt);

public sealed record ThreatSummary(IReadOnlyList<string> Unresolved, int RecentDetections);

/// <summary>
/// Decides which threats still need the user's attention.
///
/// Split out of the module deliberately: Defender exposes this across TWO WMI classes
/// that must be joined on ThreatID, the status codes are not self-explanatory, and on a
/// healthy PC there are zero threats — so without tests this logic would ship having
/// never once executed. It is exactly the code that must work on the worst day.
/// </summary>
public static class ThreatAssessment
{
    /// <summary>The window "Threats caught in the last 90 days" counts over.</summary>
    public const int RecentWindowDays = 90;

    /// <summary>Defender's documented default for ScanPurgeItemsAfterDelay, used when the setting has no value.</summary>
    public const uint DefaultPurgeDays = 15;

    /// <summary>
    /// How far back Defender's detection history goes (REMEDIES-PLAN M1). Defender removes items
    /// from its scan history after ScanPurgeItemsAfterDelay days, 15 unless changed, and 0 keeps
    /// them (https://learn.microsoft.com/powershell/module/defender/set-mppreference, checked
    /// 2026-10-28). So with Defender's defaults "the last 90 days" has been the last 15. A history
    /// cannot be older than the install either: on the dev T470p on 2026-10-28, installed 9 days
    /// earlier, it covered 9. <paramref name="purgeDays"/> is null when the setting could not be
    /// read, and the span is then unknown.
    /// </summary>
    /// <param name="installedDaysAgo">Whole days since Windows was installed (<see cref="InstallAge.DaysSince"/>), or null when not read.</param>
    public static RecordSpan DetectionHistorySpan(uint? purgeDays, int? installedDaysAgo)
    {
        const string source = "Defender's detection history";
        if (purgeDays is not { } purge) return RecordSpan.Unknown(source, "how long Defender keeps it could not be read");
        if (installedDaysAgo is { } installed && installed < RecentWindowDays && (purge == 0 || installed < purge))
            return RecordSpan.Kept(source, installed, InstallAge.Installed(installed));
        return purge == 0 || purge >= RecentWindowDays
            ? RecordSpan.Whole(source)
            : RecordSpan.Kept(source, (int)purge, string.Create(CultureInfo.InvariantCulture,
                $"Defender removes detections from it after {purge} days, its ScanPurgeItemsAfterDelay setting"));
    }

    /// <summary>
    /// ThreatStatusID values meaning the threat was NOT dealt with: still merely detected,
    /// explicitly allowed by someone, or a remediation that failed outright.
    /// </summary>
    private static readonly HashSet<uint> UnresolvedStatuses = [1, 5, 102, 103, 104, 105, 107];

    public static ThreatSummary Summarize(
        IEnumerable<ThreatRecord> threats,
        IEnumerable<DetectionRecord> detections,
        DateTime now,
        int recentWindowDays = RecentWindowDays)
    {
        var cutoff = now.AddDays(-recentWindowDays);
        var latestByThreat = new Dictionary<string, DetectionRecord>(StringComparer.Ordinal);
        var recent = 0;

        foreach (var detection in detections)
        {
            if (detection.DetectedAt is { } when && when >= cutoff) recent++;

            if (!latestByThreat.TryGetValue(detection.ThreatId, out var existing)
                || detection.DetectedAt > existing.DetectedAt)
            {
                latestByThreat[detection.ThreatId] = detection;
            }
        }

        var unresolved = new List<string>();
        foreach (var threat in threats)
        {
            var status = latestByThreat.TryGetValue(threat.ThreatId, out var detection) ? detection.StatusId : 0u;
            if (!threat.IsActive && !UnresolvedStatuses.Contains(status)) continue;

            var reason = threat.IsActive && status == 0 ? "still active" : DescribeStatus(status);
            unresolved.Add($"{threat.Name} ({reason})");
        }

        return new ThreatSummary(unresolved, recent);
    }

    /// <summary>
    /// What Defender recorded at or after <paramref name="since"/> — the outcome of a scan
    /// the user just started, as opposed to the whole history. Detections with no timestamp
    /// are left out: an undated record cannot be attributed to this scan, and claiming it
    /// was would be worse than saying nothing.
    /// </summary>
    public static IReadOnlyList<string> DetectionsSince(
        IEnumerable<ThreatRecord> threats,
        IEnumerable<DetectionRecord> detections,
        DateTime since)
    {
        var namesById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var threat in threats) namesById[threat.ThreatId] = threat.Name;

        return
        [
            .. detections
                .Where(d => d.DetectedAt is { } when && when >= since)
                .OrderBy(d => d.DetectedAt)
                .Select(d => $"{namesById.GetValueOrDefault(d.ThreatId, "unnamed threat")} ({DescribeStatus(d.StatusId)})")
        ];
    }

    public static string DescribeStatus(uint statusId) => statusId switch
    {
        1 => "detected, not yet dealt with",
        2 => "cleaned",
        3 => "quarantined",
        4 => "removed",
        5 => "allowed by an exclusion or user choice",
        6 => "blocked",
        102 => "quarantine failed",
        103 => "removal failed",
        104 => "allow action failed",
        105 => "abandoned",
        107 => "block failed",
        _ => $"status {statusId}",
    };
}
