using PcHealth.Core.Metrics;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows.Security;
using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// REMEDIES-PLAN Step A row 6 (M1): every "last N days" count states how many days its records
/// really cover. Held in both directions: a long log changes nothing at all; a short log says so,
/// keeps every count above zero graded, and stops a zero from a few days of records reading as an
/// all-clear, without raising the "could not finish" banner.
/// </summary>
public sealed class RecordSpanTests
{
    private static readonly VerdictEngine Engine = new(RulesLoader.LoadEmbedded("windows"));

    /// <summary>A day the app can run on: the clock check refuses one earlier than the build.</summary>
    private static readonly DateTime Now =
        WindowsSupport.CheckedOn.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc).AddDays(30);

    private const string SystemLog = "the System event log";

    private static Grade GradeOf(Metric metric) => Engine.EvaluateOne(metric).Grade;

    private static RecordSpan LogStarting(double daysAgo) => RecordSpan.FromOldest(SystemLog, Now.AddDays(-daysAgo), Now);

    private static Metric BlueScreens(double count) =>
        new("events.bugcheck_90d", "Blue screens (90 days)", count, HealthCategory.Stability, Detail: "Recorded bugchecks (BSOD).");

    private static Metric LowMemory(double count) =>
        new("memory.low_memory_events_30d", "Low-memory warnings (30 days)", count, HealthCategory.Memory, Detail: "Event 2004.");

    // --- A log that covers the window: exactly as before ----------------------------------------

    [Theory]
    [InlineData(90.0, 0.0)]
    [InlineData(400.0, 0.0)]
    [InlineData(400.0, 3.0)]
    public void A_log_that_covers_the_window_leaves_the_count_exactly_as_it_was(double logDays, double count)
    {
        var span = LogStarting(logDays);
        var metric = BlueScreens(count);

        Assert.Same(metric, span.Apply(metric, 90));
        Assert.Null(span.Note());
    }

    [Fact]
    public void A_whole_history_changes_nothing()
    {
        var metric = BlueScreens(0);
        Assert.Same(metric, RecordSpan.Whole("Defender's detection history").Apply(metric, 90));
    }

    // --- A short log, a week or more: graded, with its real span --------------------------------

    [Fact]
    public void A_log_of_9_days_says_so_and_a_zero_from_it_is_still_graded()
    {
        // The dev T470p on 2026-10-28: installed on 2026-10-19, its System log went back 9 days.
        var span = LogStarting(9.4);
        var counted = span.Apply(BlueScreens(0), 90);

        Assert.Equal(9, span.Days);
        Assert.False(counted.InfoOnly);
        Assert.Equal(Grade.Good, GradeOf(counted));
        Assert.Equal("Recorded bugchecks (BSOD). In the last 90 days (records cover only 9 days: " +
                     "the System event log goes back no further).", counted.Detail);
        Assert.Null(span.Note());
    }

    // --- A log under a week: a zero is not judged, a count above zero is --------------------------

    [Fact]
    public void A_zero_from_2_days_of_log_is_shown_but_not_graded_with_a_note()
    {
        var span = LogStarting(2.5);
        var counted = span.Apply(BlueScreens(0), 90);

        Assert.True(counted.InfoOnly);
        Assert.Equal(Grade.Unknown, GradeOf(counted));
        Assert.Equal(0d, counted.Value.Number);
        Assert.EndsWith("In the last 90 days (records cover only 2 days: the System event log goes back no further). " +
                        "Not judged: only 2 days of history, so zero is not an all-clear.", counted.Detail, StringComparison.Ordinal);
        Assert.Equal("Not judged: only 2 days of history in the System event log, so a zero there is not an all-clear: " +
                     "Blue screens (90 days). Scan again after two or three weeks of normal use.", span.Note());
    }

    [Theory]
    [InlineData(1.0, Grade.Normal)]
    [InlineData(4.0, Grade.Bad)]
    public void A_count_above_zero_from_a_short_log_keeps_its_grade(double count, Grade expected)
    {
        var span = LogStarting(2.5);
        var counted = span.Apply(BlueScreens(count), 90);

        Assert.False(counted.InfoOnly);
        Assert.Equal(expected, GradeOf(counted));
        Assert.Contains("records cover only 2 days", counted.Detail!, StringComparison.Ordinal);
        Assert.Null(span.Note());
    }

    // --- A count whose Good band is above zero: the same rule, at the top of that band ----------

    private static Metric AppCrashes(double count) =>
        new("events.app_crashes_7d", "Application crashes (7 days)", count, HealthCategory.Stability, Detail: "Crashes this week.");

    private const double AppCrashesGoodUpTo = 2; // EventsModule passes the same; the test below holds the rule to it

    [Fact]
    public void The_app_crash_rule_is_Good_up_to_the_count_the_events_check_passes()
    {
        // EventsModule hands RecordSpan.Apply the top of this count's Good band. If the rule's band
        // moves, that number must move with it, or a short log grades a count inside the new band.
        Assert.Equal(Grade.Good, GradeOf(AppCrashes(AppCrashesGoodUpTo)));
        Assert.Equal(Grade.Normal, GradeOf(AppCrashes(AppCrashesGoodUpTo + 1)));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void A_count_inside_a_good_band_above_zero_from_a_short_log_is_not_graded(double count)
    {
        // A freshly reimaged laptop: the Application log covers 1 day. Two crashes in one day is
        // not a clean week, and the zero beside it is not graded either.
        var span = RecordSpan.FromOldest("the Application event log", Now.AddDays(-1.3), Now);
        var counted = span.Apply(AppCrashes(count), 7, AppCrashesGoodUpTo);

        Assert.True(counted.InfoOnly);
        Assert.Equal(Grade.Unknown, GradeOf(counted));
        Assert.Equal(count, counted.Value.Number);
        Assert.EndsWith("Not judged: only 1 day of history, so a count this low is not an all-clear.", counted.Detail, StringComparison.Ordinal);
        Assert.Equal("Not judged: only 1 day of history in the Application event log, so a count this low there is not an " +
                     "all-clear: Application crashes (7 days). Scan again after two or three weeks of normal use.", span.Note());
    }

    [Fact]
    public void A_count_above_a_good_band_from_a_short_log_keeps_its_grade()
    {
        var span = RecordSpan.FromOldest("the Application event log", Now.AddDays(-1.3), Now);
        var counted = span.Apply(AppCrashes(AppCrashesGoodUpTo + 1), 7, AppCrashesGoodUpTo);

        Assert.False(counted.InfoOnly);
        Assert.Equal(Grade.Normal, GradeOf(counted));
        Assert.Null(span.Note());
    }

    [Fact]
    public void A_count_inside_its_good_band_from_a_log_of_a_week_or_more_is_graded()
    {
        var span = RecordSpan.FromOldest("the Application event log", Now.AddDays(-9.4), Now);
        var counted = span.Apply(AppCrashes(2), 30, AppCrashesGoodUpTo);

        Assert.False(counted.InfoOnly);
        Assert.Equal(Grade.Good, GradeOf(counted));
        Assert.Null(span.Note());
    }

    [Fact]
    public void One_note_names_every_zero_the_log_was_too_short_for_and_never_splits_in_the_kit()
    {
        var span = LogStarting(1.2);
        span.Apply(LowMemory(0), 30);
        span.Apply(BlueScreens(0), 90);
        span.Apply(BlueScreens(2), 90);
        // Information only already: shown with its span, never listed as left ungraded.
        var limits = span.Apply(new Metric("events.cpu_speed_limited_30d", "Processor speed limited by firmware (30 days)", 0,
            HealthCategory.Performance, InfoOnly: true), 30);

        var note = span.Note()!;
        Assert.Contains("only 1 day of history", note, StringComparison.Ordinal);
        Assert.Contains("Low-memory warnings (30 days), Blue screens (90 days).", note, StringComparison.Ordinal);
        Assert.DoesNotContain("Processor speed", note, StringComparison.Ordinal);
        Assert.DoesNotContain("; ", note, StringComparison.Ordinal); // the kit splits a row's notes on it
        Assert.True(limits.InfoOnly);
        Assert.Equal("In the last 30 days (records cover only 1 day: the System event log goes back no further).", limits.Detail);
    }

    [Fact]
    public void An_empty_log_covers_less_than_a_day()
    {
        var span = RecordSpan.FromOldest(SystemLog, null, Now);
        var counted = span.Apply(BlueScreens(0), 90);

        Assert.Equal(0, span.Days);
        Assert.True(counted.InfoOnly);
        Assert.Contains("records cover only less than a day: the System event log is empty", counted.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_seven_day_window_on_a_log_of_exactly_a_week_is_covered()
    {
        var metric = new Metric("events.app_crashes_7d", "Application crashes (7 days)", 0, HealthCategory.Stability);
        Assert.Same(metric, RecordSpan.FromOldest("the Application event log", Now.AddDays(-7.2), Now).Apply(metric, 7));
    }

    // --- A clock that looks wrong: the span is unknown -------------------------------------------

    [Fact]
    public void A_log_dated_after_the_clock_has_an_unknown_span_and_its_zero_is_not_judged()
    {
        var span = RecordSpan.FromOldest(SystemLog, Now.AddDays(3), Now);
        var zero = span.Apply(LowMemory(0), 30);
        var two = span.Apply(LowMemory(2), 30);

        Assert.Null(span.Days);
        Assert.Equal(Grade.Unknown, GradeOf(zero));
        Assert.Contains("how many days the records cover is unknown: its oldest record is dated later than the clock",
            zero.Detail!, StringComparison.Ordinal);
        Assert.Contains("Not judged: with the span unknown, zero is not an all-clear.", zero.Detail!, StringComparison.Ordinal);
        Assert.Equal(Grade.Bad, GradeOf(two)); // what was found was found
        Assert.StartsWith("Not judged: how many days the System event log covers is unknown (", span.Note(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_clock_set_before_this_app_was_built_gives_an_unknown_span()
    {
        var clockSetBack = WindowsSupport.CheckedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-30);
        var span = RecordSpan.FromOldest(SystemLog, clockSetBack.AddDays(-200), clockSetBack);

        Assert.Null(span.Days);
        Assert.Contains("the clock is set earlier than this app was built", span.Describe(90), StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_that_could_not_be_read_has_an_unknown_span()
    {
        var span = RecordSpan.Unknown(SystemLog, "it could not be read");
        Assert.True(span.Apply(BlueScreens(0), 90).InfoOnly);
        Assert.Contains("unknown (it could not be read)", span.Note(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_span_is_given_in_days_never_as_a_date()
    {
        // On a new install the oldest record is the install date, which the report never carries.
        var text = LogStarting(3).Describe(90) + LogStarting(3).Apply(BlueScreens(0), 90).Detail;
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", text);
    }

    // --- Defender's detection history ---------------------------------------------------------

    [Fact]
    public void With_Defenders_defaults_the_90_day_count_covers_15_days_and_a_zero_stays_graded()
    {
        var span = ThreatAssessment.DetectionHistorySpan(ThreatAssessment.DefaultPurgeDays, installedDaysAgo: null);
        var counted = span.Apply(new Metric("malware.recent_detections_90d", "Threats caught in the last 90 days", 0,
            HealthCategory.Security, Detail: "Nothing has been detected recently."), ThreatAssessment.RecentWindowDays);

        Assert.Equal(15, span.Days);
        Assert.Equal(Grade.Good, GradeOf(counted));
        Assert.Equal("Nothing has been detected recently. In the last 90 days (records cover only 15 days: Defender removes " +
                     "detections from it after 15 days, its ScanPurgeItemsAfterDelay setting).", counted.Detail);
    }

    [Theory]
    [InlineData(0u)]   // 0 keeps detections for good
    [InlineData(90u)]
    [InlineData(365u)]
    public void A_history_kept_for_the_whole_window_changes_nothing(uint purgeDays)
    {
        var metric = new Metric("malware.recent_detections_90d", "Threats caught in the last 90 days", 0, HealthCategory.Security);
        Assert.Same(metric, ThreatAssessment.DetectionHistorySpan(purgeDays, installedDaysAgo: null).Apply(metric, 90));
    }

    [Theory]
    [InlineData(400, 15)]
    [InlineData(9, 9)] // the dev T470p on 2026-10-28
    public void The_history_is_no_older_than_the_install(int installedDaysAgo, int expected)
    {
        var span = ThreatAssessment.DetectionHistorySpan(ThreatAssessment.DefaultPurgeDays, installedDaysAgo);
        var counted = span.Apply(new Metric("malware.recent_detections_90d", "Threats caught in the last 90 days", 0,
            HealthCategory.Security), 90);

        Assert.Equal(expected, span.Days);
        Assert.Equal(Grade.Good, GradeOf(counted)); // a week or more of history: still judged
    }

    [Fact]
    public void An_old_install_with_detections_kept_for_good_changes_nothing()
    {
        var metric = new Metric("malware.recent_detections_90d", "Threats caught in the last 90 days", 0, HealthCategory.Security);
        Assert.Same(metric, ThreatAssessment.DetectionHistorySpan(0, installedDaysAgo: 400).Apply(metric, 90));
    }

    [Theory]
    [InlineData(15u)]
    [InlineData(0u)]
    public void On_a_3_day_old_install_a_zero_is_not_judged(uint purgeDays)
    {
        var span = ThreatAssessment.DetectionHistorySpan(purgeDays, installedDaysAgo: 3);
        var counted = span.Apply(new Metric("malware.recent_detections_90d", "Threats caught in the last 90 days", 0,
            HealthCategory.Security), 90);

        Assert.Equal(3, span.Days);
        Assert.Equal(Grade.Unknown, GradeOf(counted));
        Assert.Contains("records cover only 3 days: Windows was installed 3 days ago", counted.Detail!, StringComparison.Ordinal);
        Assert.StartsWith("Not judged: only 3 days of history in Defender's detection history", span.Note(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_purge_setting_that_could_not_be_read_leaves_a_zero_ungraded()
    {
        var span = ThreatAssessment.DetectionHistorySpan(null, installedDaysAgo: null);
        var counted = span.Apply(new Metric("malware.recent_detections_90d", "Threats caught in the last 90 days", 0,
            HealthCategory.Security), 90);

        Assert.Null(span.Days);
        Assert.Equal(Grade.Unknown, GradeOf(counted));
    }
}
