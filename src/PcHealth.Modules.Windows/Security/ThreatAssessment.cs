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
    /// <summary>
    /// ThreatStatusID values meaning the threat was NOT dealt with: still merely detected,
    /// explicitly allowed by someone, or a remediation that failed outright.
    /// </summary>
    private static readonly HashSet<uint> UnresolvedStatuses = [1, 5, 102, 103, 104, 105, 107];

    public static ThreatSummary Summarize(
        IEnumerable<ThreatRecord> threats,
        IEnumerable<DetectionRecord> detections,
        DateTime now,
        int recentWindowDays = 90)
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
