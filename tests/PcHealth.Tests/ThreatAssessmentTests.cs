using PcHealth.Modules.Windows.Security;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Regression cover for the threat-history logic. On a healthy PC there are zero threats,
/// so without these tests this code would ship having never executed — and the one time it
/// broke (ThreatStatusID queried on the wrong WMI class) it failed silently, reporting a
/// clean bill of health while checking nothing.
/// </summary>
public class ThreatAssessmentTests
{
    private static readonly DateTime Now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Local);

    private static ThreatRecord Threat(string id, string name, bool active = false) => new(id, name, active);

    private static DetectionRecord Detection(string threatId, uint status, int daysAgo = 1) =>
        new(threatId, status, Now.AddDays(-daysAgo));

    [Fact]
    public void A_clean_machine_reports_nothing()
    {
        var summary = ThreatAssessment.Summarize([], [], Now);
        Assert.Empty(summary.Unresolved);
        Assert.Equal(0, summary.RecentDetections);
    }

    [Fact]
    public void A_quarantined_threat_is_not_flagged_as_needing_attention()
    {
        // Status 3 = quarantined: Defender dealt with it. Counting it as outstanding
        // would nag the user about something already handled.
        var summary = ThreatAssessment.Summarize(
            [Threat("T1", "Trojan:Win32/Example")],
            [Detection("T1", status: 3)], Now);

        Assert.Empty(summary.Unresolved);
        Assert.Equal(1, summary.RecentDetections);   // still worth telling them it happened
    }

    [Theory]
    [InlineData(1u, "detected, not yet dealt with")]
    [InlineData(5u, "allowed")]
    [InlineData(102u, "quarantine failed")]
    [InlineData(103u, "removal failed")]
    [InlineData(107u, "block failed")]
    public void Unhandled_outcomes_are_surfaced_with_a_readable_reason(uint status, string expectedFragment)
    {
        var summary = ThreatAssessment.Summarize(
            [Threat("T1", "Trojan:Win32/Example")],
            [Detection("T1", status)], Now);

        var entry = Assert.Single(summary.Unresolved);
        Assert.Contains("Trojan:Win32/Example", entry, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, entry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_active_threat_counts_even_with_no_detection_record()
    {
        var summary = ThreatAssessment.Summarize([Threat("T9", "Backdoor:Win32/X", active: true)], [], Now);

        var entry = Assert.Single(summary.Unresolved);
        Assert.Contains("still active", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void The_join_uses_thread_ids_not_positions()
    {
        // The two WMI classes are unordered and different lengths; matching by index
        // would attribute one threat's outcome to another.
        var summary = ThreatAssessment.Summarize(
            [Threat("A", "Cleaned.Threat"), Threat("B", "Allowed.Threat")],
            [Detection("B", status: 5), Detection("A", status: 3)], Now);

        var entry = Assert.Single(summary.Unresolved);
        Assert.Contains("Allowed.Threat", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void The_most_recent_detection_decides_the_outcome()
    {
        // Detected last week, quarantined today: it is handled.
        var summary = ThreatAssessment.Summarize(
            [Threat("T1", "Trojan:Win32/Example")],
            [Detection("T1", status: 1, daysAgo: 7), Detection("T1", status: 3, daysAgo: 0)], Now);

        Assert.Empty(summary.Unresolved);
    }

    [Fact]
    public void Recent_detections_respect_the_time_window()
    {
        var detections = new[]
        {
            Detection("T1", 3, daysAgo: 10),
            Detection("T2", 3, daysAgo: 89),
            Detection("T3", 3, daysAgo: 91),     // outside the 90-day window
        };

        Assert.Equal(2, ThreatAssessment.Summarize([], detections, Now).RecentDetections);
    }

    [Fact]
    public void Detections_without_a_timestamp_are_not_counted_as_recent()
    {
        var summary = ThreatAssessment.Summarize([], [new DetectionRecord("T1", 3, null)], Now);
        Assert.Equal(0, summary.RecentDetections);
    }

    [Fact]
    public void Every_documented_status_has_wording_and_none_leaks_a_raw_number()
    {
        foreach (var status in new uint[] { 1, 2, 3, 4, 5, 6, 102, 103, 104, 105, 107 })
        {
            var text = ThreatAssessment.DescribeStatus(status);
            Assert.DoesNotContain("status ", text, StringComparison.Ordinal);
        }
        // Anything genuinely unknown still degrades gracefully rather than throwing.
        Assert.Contains("999", ThreatAssessment.DescribeStatus(999), StringComparison.Ordinal);
    }
}
