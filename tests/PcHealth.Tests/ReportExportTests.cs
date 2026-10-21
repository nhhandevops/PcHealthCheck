using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;
using Xunit;

namespace PcHealth.Tests;

public class ReportExportTests
{
    private static ScanReport Report(
        IReadOnlyList<TrendFinding>? trends = null,
        HistoryCoverage? history = null,
        IReadOnlyList<Verdict>? verdicts = null)
    {
        verdicts ??= [new Verdict(
            new Metric("storage.volume.free_pct", "Free space", 8.5, HealthCategory.Storage, Unit: "%", Subject: "C:"),
            Grade.Bad, "rule.free", ["Free up space"], "It will get worse")];

        return new ScanReport
        {
            GeneratedAt = DateTimeOffset.Now,
            MachineName = "TESTPC",
            OsDescription = "Windows 11",
            AppVersion = "0.1.0",
            RanElevated = false,
            Modules = [new ModuleRunSummary("volumes", "Volumes", ModuleStatus.Completed, TimeSpan.FromSeconds(1), null, [])],
            Verdicts = verdicts,
            Receipt = [],
            Trends = trends ?? [],
            History = history,
            CategoryGrades = ScanReport.ComputeCategoryGrades(verdicts),
            OverallGrade = ScanReport.Worst(verdicts.Select(v => v.Grade)),
        };
    }

    [Fact]
    public void Renders_trend_findings_with_their_tips()
    {
        var trend = new TrendFinding(
            TrendKind.CounterGrowth, "smart.pending_sectors", "ST2000 — 2000 GB", "Pending sectors",
            Grade.Bad, "This is new: sectors have become unstable.", 0, 3,
            DateTimeOffset.Now.AddDays(-7), ["BACK UP THIS DRIVE NOW."]);

        var html = HtmlReportExporter.ToHtml(Report([trend]));

        Assert.Contains("What changed since your last scan", html, StringComparison.Ordinal);
        Assert.Contains("Pending sectors", html, StringComparison.Ordinal);
        Assert.Contains("ST2000 — 2000 GB", html, StringComparison.Ordinal);
        Assert.Contains("BACK UP THIS DRIVE NOW.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_so_plainly_when_nothing_moved()
    {
        var html = HtmlReportExporter.ToHtml(
            Report(history: new HistoryCoverage(4, DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now)));

        Assert.Contains("Nothing has moved", html, StringComparison.Ordinal);
        Assert.Contains("3 earlier scan(s)", html, StringComparison.Ordinal); // 4 recorded, 3 to compare against
        Assert.Contains("over 30 days", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_that_a_first_scan_has_nothing_to_compare()
    {
        var html = HtmlReportExporter.ToHtml(
            Report(history: new HistoryCoverage(1, DateTimeOffset.Now, DateTimeOffset.Now)));

        Assert.Contains("first recorded scan", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing has moved", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scan_that_recorded_no_history_does_not_claim_to_be_the_first()
    {
        // --no-history: nothing was stored, so "first recorded scan, run again" was untrue (HW-001).
        var html = HtmlReportExporter.ToHtml(Report(history: HistoryOutcome.Disabled.Coverage));

        Assert.DoesNotContain("first recorded scan", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not added to the history", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Escapes_content_so_a_device_name_cannot_inject_markup()
    {
        var verdicts = new[]
        {
            new Verdict(
                new Metric("startup.count", "Startup items", 3, HealthCategory.Security,
                    Subject: "<script>alert('x')</script>", Detail: "a & b < c"),
                Grade.Good, null, [], null),
        };

        var html = HtmlReportExporter.ToHtml(Report(verdicts: verdicts));

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("a &amp; b &lt; c", html, StringComparison.Ordinal);
    }

    [Fact]
    public void States_the_zero_network_promise_and_stays_self_contained()
    {
        var html = HtmlReportExporter.ToHtml(Report());

        Assert.Contains("Network connections made by PcHealthCheck during this scan: 0", html, StringComparison.Ordinal);
        // A self-contained file: no external stylesheets, scripts, or images.
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Checks_that_could_not_finish_are_called_out_before_the_results()
    {
        // The dangerous failure mode: a check silently drops out, and its absence reads
        // as "nothing wrong here". The report must say so, above the findings.
        var report = Report() with
        {
            Modules =
            [
                new ModuleRunSummary("malware.defender", "Malware history & defences",
                    ModuleStatus.PartiallyCompleted, TimeSpan.FromSeconds(1), null,
                    ["Threat history could not be read"]),
                new ModuleRunSummary("volumes", "Volumes", ModuleStatus.Completed, TimeSpan.Zero, null, []),
            ],
        };

        var html = HtmlReportExporter.ToHtml(report);

        Assert.Contains("Some checks could not finish", html, StringComparison.Ordinal);
        Assert.Contains("Malware history &amp; defences", html, StringComparison.Ordinal);
        Assert.Contains("Threat history could not be read", html, StringComparison.Ordinal);
        Assert.Contains("not the same as being healthy", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Found in acceptance testing, and the fourth time this app has presented unchecked as
    /// clean: the user selected an admin check, declined the Windows prompt, and the report
    /// said "GOOD" with nothing to indicate an entire check had never run. The headline
    /// grade is a verdict on the checks that ran, and has to say so when some did not.
    /// </summary>
    [Fact]
    public void The_headline_grade_is_marked_incomplete_when_a_check_did_not_run()
    {
        var report = Report(verdicts:
        [
            new Verdict(new Metric("storage.volume.free_pct", "Free space", 90, HealthCategory.Storage,
                Unit: "%", Subject: "C:"), Grade.Good, null, [], null),
        ]) with
        {
            Modules =
            [
                new ModuleRunSummary("volumes", "Volumes", ModuleStatus.Completed, TimeSpan.Zero, null, []),
                new ModuleRunSummary("disk.smart", "Drive SMART health", ModuleStatus.Failed, TimeSpan.Zero,
                    "Administrator access was declined, so the admin checks were skipped.", []),
            ],
        };

        var html = HtmlReportExporter.ToHtml(report);

        Assert.Contains("INCOMPLETE", html, StringComparison.Ordinal);
        Assert.Contains("Some checks could not finish", html, StringComparison.Ordinal);
        Assert.Contains("Administrator access was declined", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_scan_is_not_labelled_incomplete()
    {
        Assert.DoesNotContain("INCOMPLETE", HtmlReportExporter.ToHtml(Report()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fully_successful_scan_shows_no_incomplete_warning()
    {
        var html = HtmlReportExporter.ToHtml(Report());
        Assert.DoesNotContain("Some checks could not finish", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_export_round_trips_the_report()
    {
        var trend = new TrendFinding(
            TrendKind.Projection, "storage.wear_pct", "SSD", "SSD endurance used",
            Grade.Normal, "reaching rated endurance around March 2028", 10, 30,
            DateTimeOffset.Now.AddDays(-100), ["Plan a replacement"]);

        var json = JsonReportExporter.ToJson(Report([trend]));

        Assert.Contains("\"Trends\"", json, StringComparison.Ordinal);
        Assert.Contains("Projection", json, StringComparison.Ordinal);
        Assert.Contains("storage.wear_pct", json, StringComparison.Ordinal);
    }
}
