using PcHealth.Modules.Windows.Security;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// The branches that matter here — malware found, remediation failed, scan errored — never
/// execute on a healthy development machine. Same reason as <see cref="ThreatAssessmentTests"/>:
/// untested code that only runs on the worst day is code that has never run at all.
/// </summary>
public class DefenderScanTests
{
    private static readonly DateTime ScanStart = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void The_newest_platform_wins_even_when_string_order_disagrees()
    {
        // "4.18.9.0" sorts after "4.18.10.0" as text, which would silently run a stale
        // Defender platform build.
        Assert.Equal("4.18.10.0-0", DefenderScanAssessment.PickNewestPlatform(
            ["4.18.9.0-0", "4.18.10.0-0", "4.18.2.5-0"]));
    }

    [Fact]
    public void The_trailing_revision_breaks_ties()
    {
        Assert.Equal("4.18.26070.9-3", DefenderScanAssessment.PickNewestPlatform(
            ["4.18.26070.9-0", "4.18.26070.9-3", "4.18.26070.9-1"]));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    public void Unparseable_folder_names_are_ignored_rather_than_chosen(string junk)
    {
        Assert.Equal("4.18.1.0", DefenderScanAssessment.PickNewestPlatform([junk, "4.18.1.0"]));
        Assert.Null(DefenderScanAssessment.PickNewestPlatform([junk]));
    }

    [Fact]
    public void Nothing_found_is_stated_without_overclaiming()
    {
        var text = Describe(DefenderScanKind.Quick, 0, []);

        Assert.Contains("found nothing", text, StringComparison.Ordinal);
        // A quick scan is not a full scan, and the wording must not let it read as one.
        Assert.Contains("not a check of every file", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_handled_detection_still_tells_the_user_something_got_in()
    {
        var text = Describe(DefenderScanKind.Quick, 0, ["Trojan:Win32/Example (quarantined)"]);

        Assert.Contains("Trojan:Win32/Example", text, StringComparison.Ordinal);
        Assert.Contains("did reach this PC", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_code_two_never_reads_as_a_clean_result()
    {
        var text = Describe(DefenderScanKind.Quick, DefenderScanAssessment.ThreatsFoundOrIncomplete, []);

        Assert.Contains("could NOT finish dealing", text, StringComparison.Ordinal);
        Assert.Contains("Windows Security", text, StringComparison.Ordinal);
        Assert.DoesNotContain("found nothing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_exit_code_is_reported_as_unknown_not_clean()
    {
        var text = Describe(DefenderScanKind.Quick, 1449, []);

        Assert.Contains("1449", text, StringComparison.Ordinal);
        Assert.Contains("unknown rather than clean", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_that_could_not_start_says_no_scan_ran()
    {
        var text = Describe(DefenderScanKind.Quick, -1, []);

        Assert.Contains("no scan ran", text, StringComparison.Ordinal);
        Assert.DoesNotContain("found nothing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timeout_says_the_scan_is_still_running_elsewhere()
    {
        var text = DefenderScanAssessment.Describe(
            DefenderScanKind.Quick, null, 0, timedOut: true, [], TimeSpan.FromMinutes(45));

        Assert.Contains("still running", text, StringComparison.Ordinal);
        Assert.Contains("45 minutes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("found nothing", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.4, "under a second")]
    [InlineData(1, "1 second")]
    [InlineData(52, "52 seconds")]
    [InlineData(130, "2 minutes")]
    [InlineData(60, "1 minute")]
    public void The_duration_is_reported_in_the_unit_a_person_would_use(double seconds, string expected)
    {
        // A folder scan of a small directory finishes in milliseconds; rounding that up to
        // "1 minute" was a real result on this machine, and a number that is not true
        // undermines every other number in the app.
        Assert.Equal(expected, DefenderScanAssessment.DescribeElapsed(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void A_folder_scan_promises_that_nothing_was_changed_either_way()
    {
        var clean = Describe(DefenderScanKind.Folder, 0, [], subject: @"D:\Downloads");
        var flagged = Describe(DefenderScanKind.Folder, DefenderScanAssessment.ThreatsFoundOrIncomplete, [], subject: @"D:\Downloads");

        Assert.Contains(@"D:\Downloads", clean, StringComparison.Ordinal);
        Assert.Contains("nothing was moved, changed or deleted", clean, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed or removed", flagged, StringComparison.Ordinal);
        Assert.Contains("still exactly where they were", flagged, StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_shown_to_the_user_is_the_command_that_runs()
    {
        Assert.Equal("MpCmdRun.exe -Scan -ScanType 1",
            DefenderScanRunner.DescribeCommand(DefenderScanKind.Quick, null));
        // Remediation stays off for folder scans: the app must not change the user's files.
        Assert.Equal(@"MpCmdRun.exe -Scan -ScanType 3 -File ""D:\Downloads"" -DisableRemediation",
            DefenderScanRunner.DescribeCommand(DefenderScanKind.Folder, @"D:\Downloads"));
    }

    [Fact]
    public void Only_detections_from_this_scan_are_attributed_to_it()
    {
        var threats = new[] { new ThreatRecord("T1", "Old.Threat", false), new ThreatRecord("T2", "New.Threat", false) };
        var detections = new[]
        {
            new DetectionRecord("T1", 3, ScanStart.AddDays(-30)),   // last month, not ours
            new DetectionRecord("T2", 3, ScanStart.AddSeconds(20)), // during the scan
        };

        var found = ThreatAssessment.DetectionsSince(threats, detections, ScanStart);

        var entry = Assert.Single(found);
        Assert.Contains("New.Threat", entry, StringComparison.Ordinal);
        Assert.Contains("quarantined", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undated_detection_is_not_claimed_by_this_scan()
    {
        var found = ThreatAssessment.DetectionsSince(
            [new ThreatRecord("T1", "Undated.Threat", false)],
            [new DetectionRecord("T1", 3, null)],
            ScanStart);

        Assert.Empty(found);
    }

    private static string Describe(DefenderScanKind kind, int exitCode, IReadOnlyList<string> detections, string? subject = null) =>
        DefenderScanAssessment.Describe(kind, subject, exitCode, timedOut: false, detections, TimeSpan.FromMinutes(2));
}
