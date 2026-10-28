using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Text;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows.Security;
using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// A freshly installed, healthy Windows must read clean (REMEDIES-PLAN Step A rows 10, 11 and 13).
/// Each change is locked in both directions: the new install, or the workgroup PC, stops raising
/// alarms, and the PC where the same reading means something keeps its grade.
/// </summary>
public sealed class FreshInstallTests
{
    private static readonly VerdictEngine Engine = new(RulesLoader.LoadEmbedded("windows"));

    /// <summary>A day the app can run on: the install-age floor never moves a clock past this.</summary>
    private static readonly DateTime Now =
        WindowsSupport.CheckedOn.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc).AddDays(30);

    // --- Row 10: how old the install is ----------------------------------------------------------

    [Theory]
    [InlineData(0.2, 0)]
    [InlineData(3.0, 3)]
    [InlineData(6.9, 6)]
    public void An_install_under_a_week_old_is_young(double daysAgo, int expected)
    {
        Assert.Equal(expected, InstallAge.Young(Now.AddDays(-daysAgo), Now));
    }

    [Theory]
    [InlineData(7.0)]
    [InlineData(30.0)]
    [InlineData(400.0)]
    public void An_install_a_week_old_or_more_is_not(double daysAgo)
    {
        Assert.Equal((int)daysAgo, InstallAge.DaysSince(Now.AddDays(-daysAgo), Now));
        Assert.Null(InstallAge.Young(Now.AddDays(-daysAgo), Now));
    }

    [Fact]
    public void No_install_date_or_one_after_the_clock_is_never_young()
    {
        Assert.Null(InstallAge.Young(null, Now));
        // The clock and the install date disagree, so one of them is wrong: judge as usual.
        Assert.Null(InstallAge.DaysSince(Now.AddHours(1), Now));
        Assert.Null(InstallAge.Young(Now.AddDays(2), Now));
    }

    [Fact]
    public void A_clock_set_back_cannot_make_an_old_install_look_new()
    {
        // Installed a year before the app was built; the clock has been set back to two days after
        // the install. Measured against that clock it would be young.
        var installed = WindowsSupport.CheckedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddYears(-1);
        var clockSetBack = installed.AddDays(2);

        Assert.Null(InstallAge.Young(installed, clockSetBack));
        Assert.True(InstallAge.DaysSince(installed, clockSetBack) >= 365);
    }

    [Theory]
    [InlineData(0, "Windows was installed less than a day ago")]
    [InlineData(1, "Windows was installed 1 day ago")]
    [InlineData(5, "Windows was installed 5 days ago")]
    public void The_note_says_how_long_ago(int days, string expected)
    {
        Assert.Equal(expected, InstallAge.Installed(days));
    }

    // --- Row 10: Defender's quick scan ----------------------------------------------------------

    [Fact]
    public void A_scan_that_never_ran_on_a_young_install_is_shown_but_not_judged()
    {
        var metric = ScanRecency.QuickScan(ScanRecency.NeverSentinel, defenderInCharge: true, youngInstallDays: 3);

        Assert.True(metric.InfoOnly);
        Assert.Equal(ScanRecency.NeverText, metric.Value.Text);
        Assert.Equal(Grade.Unknown, Engine.EvaluateOne(metric).Grade);
        Assert.Contains("Windows was installed 3 days ago", metric.Detail!, StringComparison.Ordinal);
        Assert.Equal(
            "Days since last quick scan: not judged: Windows was installed 3 days ago. " +
            "On a new install, Defender's first scheduled quick scan can take several days.",
            ScanRecency.NotJudgedNote(3));
    }

    [Fact]
    public void A_scan_that_never_ran_on_an_older_install_is_still_Bad()
    {
        var metric = ScanRecency.QuickScan(ScanRecency.NeverSentinel, defenderInCharge: true, youngInstallDays: null);

        Assert.False(metric.InfoOnly);
        Assert.Equal(Grade.Bad, Engine.EvaluateOne(metric).Grade);
    }

    [Theory]
    [InlineData(0u, Grade.Good)]
    [InlineData(20u, Grade.Normal)]
    public void A_young_install_changes_nothing_but_a_scan_that_never_ran(uint days, Grade expected)
    {
        Assert.Equal(expected, Engine.EvaluateOne(ScanRecency.QuickScan(days, defenderInCharge: true, youngInstallDays: 2)).Grade);
        // A figure Defender did not return stays "not reported", which the module records as a failure.
        Assert.Equal(ScanRecency.NotReportedText, ScanRecency.QuickScan(null, defenderInCharge: true, youngInstallDays: 2).Value.Text);
    }

    // --- Row 10: Reliability Monitor's history --------------------------------------------------

    [Fact]
    public void An_empty_reliability_history_on_a_young_install_is_a_note_not_a_failure()
    {
        var (status, note) = ReliabilityHistory.WhenEmpty(2);

        // Skipped does not raise the "some checks could not finish" banner; PartiallyCompleted does.
        Assert.Equal(ModuleStatus.Skipped, status);
        Assert.StartsWith("Reliability history: not judged: Windows was installed 2 days ago.", note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_reliability_history_on_an_older_install_still_says_the_check_could_not_finish()
    {
        var (status, note) = ReliabilityHistory.WhenEmpty(null);

        Assert.Equal(ModuleStatus.PartiallyCompleted, status);
        Assert.Equal(ReliabilityHistory.DisabledNote, note);
    }

    // --- Row 11: the time service ---------------------------------------------------------------

    /// <summary>
    /// W32Time is "Manual" and "Stopped" on a PC outside a domain by design, so "stopped" put a
    /// Normal row on every healthy workgroup PC. The module now marks the reading information only,
    /// and no rule grades it either way, so nothing re-grades it if that flag is ever lost.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_time_service_state_is_never_graded(bool running)
    {
        var verdict = Engine.EvaluateOne(new Metric("time.sync.service_running", "Time service running", running, HealthCategory.SoftwareOs));

        Assert.Equal(Grade.Unknown, verdict.Grade);
        Assert.Null(verdict.RuleId);
    }

    // --- Row 13: menu paths that are wrong on one Windows -----------------------------------------

    /// <summary>
    /// Paths found in the tips on 2026-10-27, each missing from one Windows version: Windows 10 has
    /// Activation, Troubleshoot and Windows Update under Update &amp; Security, and Windows 11 has no
    /// "Update &amp; Security" at all. "Privacy &amp; security" and "Startup apps" exist only on
    /// Windows 11.
    /// </summary>
    private static readonly string[] OneVersionOnly =
    [
        "Settings > System > Activation",
        "Settings > System > Troubleshoot",
        "Settings > Windows Update",
        "Settings > System > Power",
        "Settings > Update & Security",
        "Privacy & security",
        "Startup apps",
    ];

    private static IEnumerable<string> AllTips() =>
        RulesLoader.LoadAllEmbedded().SelectMany(r => r.Tips?.Values.SelectMany(t => t) ?? [])
            .Concat(RulesLoader.LoadAllEmbedded().Select(r => r.Forecast).OfType<string>());

    [Fact]
    public void No_tip_names_a_menu_path_that_only_one_Windows_version_has()
    {
        foreach (var tip in AllTips())
        {
            foreach (var path in OneVersionOnly)
                Assert.DoesNotContain(path, tip, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void No_advice_sentence_names_a_menu_path_that_only_one_Windows_version_has()
    {
        // The "What you can do" sentences live in Text/en.json, not in the rule files.
        var catalog = AdviceCatalog.English;
        foreach (var sentence in catalog.Keys.SelectMany(catalog.Templates))
        {
            foreach (var path in OneVersionOnly)
                Assert.DoesNotContain(path, sentence, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("license.activated", false, "ms-settings:activation")]
    [InlineData("updates.repeated_failures", true, "ms-settings:troubleshoot")]
    [InlineData("startup.invalid_sig_count", 1d, "ms-settings:startupapps")]
    [InlineData("os.patch.age_days", 100d, "ms-settings:windowsupdate")]
    public void The_tips_still_say_how_to_get_there(string metricId, object value, string link)
    {
        MetricValue metricValue = value switch
        {
            bool flag => flag,
            double number => number,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
        var verdict = Engine.EvaluateOne(new Metric(metricId, metricId, metricValue, HealthCategory.SoftwareOs));

        Assert.Equal(Grade.Bad, verdict.Grade);
        Assert.Contains(verdict.Tips, t => t.Contains(link, StringComparison.Ordinal));
    }
}
