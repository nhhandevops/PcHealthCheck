using PcHealth.Core.Metrics;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows.Sensors;
using PcHealth.Modules.Windows.Storage;
using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// The five wrong results a full hardware audit of a ThinkPad T470p found in the app's own
/// report (HW-001, 2026-10-21). Each fix is locked in both directions: the case that was wrong
/// must now be reported, and the ordinary healthy case must not start raising alarms.
/// </summary>
public sealed class HardwareAuditFixTests
{
    private static readonly VerdictEngine Engine = new(RulesLoader.LoadEmbedded());

    private static Grade GradeOf(string id, MetricValue value) =>
        Engine.EvaluateOne(new Metric(id, id, value, HealthCategory.Memory)).Grade;

    // --- 1. Windows 10 end of support -------------------------------------------------------

    [Fact]
    public void Windows10_end_of_support_tip_names_the_extended_date()
    {
        var verdict = Engine.EvaluateOne(new Metric("os.win10_eol_near", "eol", true, HealthCategory.SoftwareOs));

        Assert.Contains(verdict.Tips, t => t.Contains("2027-10-12", StringComparison.Ordinal));
        Assert.DoesNotContain(verdict.Tips, t => t.Contains("2026-10-13", StringComparison.Ordinal));
    }

    // --- 2. Running out of memory ------------------------------------------------------------

    [Theory]
    [InlineData(55, Grade.Good)]
    [InlineData(84, Grade.Normal)]   // the T470p's reading between its low-memory events
    [InlineData(99, Grade.Bad)]      // where Windows logged event 2004 three times
    public void Commit_charge_is_graded(double percent, Grade expected) =>
        Assert.Equal(expected, GradeOf("memory.commit_pct", percent));

    [Theory]
    [InlineData(0, Grade.Good)]
    [InlineData(1, Grade.Normal)]
    [InlineData(3, Grade.Bad)]
    public void Low_memory_warnings_are_graded(double count, Grade expected) =>
        Assert.Equal(expected, GradeOf("memory.low_memory_events_30d", count));

    // --- 3a. Processor temperature without PawnIO -------------------------------------------

    [Theory]
    [InlineData(363, 89.9)]          // the T470p's THM0 zone, while throttling
    [InlineData(318, 44.9)]
    public void A_plausible_thermal_zone_reading_is_converted(double kelvin, double celsius) =>
        Assert.Equal(celsius, SensorAnalysis.PlausibleZoneCelsius(kelvin));

    [Theory]
    [InlineData(300)]                // ~27 °C: the fixed placeholder many desktops expose
    [InlineData(0)]
    [InlineData(500)]
    public void An_implausible_thermal_zone_reading_counts_as_not_read(double kelvin) =>
        Assert.Null(SensorAnalysis.PlausibleZoneCelsius(kelvin));

    [Fact]
    public void A_hot_processor_zone_is_not_graded_good() =>
        Assert.Equal(Grade.Normal, GradeOf("cpu.zone_temperature_c", 90));

    // --- 3b. A USB enclosure's empty health record ------------------------------------------

    [Fact]
    public void An_empty_usb_bridge_record_is_a_placeholder() =>
        // Realtek RTL9210B, as Windows returned it: Wear 0, Temperature 0, nothing else.
        Assert.True(ReliabilityCounterCheck.IsPlaceholder(0, 0, null, null, null));

    [Fact]
    public void A_real_worn_ssd_is_not_a_placeholder() =>
        // Intel SSDSC2BF240A4L on the same laptop: Wear 23, 6880 power-on hours, 0 read errors.
        Assert.False(ReliabilityCounterCheck.IsPlaceholder(23, 0, 6880, 0, null));

    [Fact]
    public void A_brand_new_drive_at_zero_wear_is_not_a_placeholder() =>
        Assert.False(ReliabilityCounterCheck.IsPlaceholder(0, 38, 5, 0, 0));

    [Theory]
    [InlineData(0, true, false)]     // Seagate HDD in a USB caddy: "SSD", wear 0 - not evidence
    [InlineData(4, true, true)]      // a USB SSD that reports real wear is still graded
    [InlineData(0, false, true)]     // an internal SSD at 0% is believed
    [InlineData(23, false, true)]    // the T470p's internal Intel SSD
    public void Zero_wear_over_usb_is_not_graded(int wear, bool onUsb, bool trusted) =>
        Assert.Equal(trusted, ReliabilityCounterCheck.WearIsTrustworthy((ushort)wear, onUsb));

    // --- Battery: how the laptop is actually used --------------------------------------------

    [Fact]
    public void A_laptop_that_never_left_the_charger_shows_zero_time_on_battery()
    {
        // The T470p's report: three days, all on AC.
        var share = BatteryUsage.Summarize([("PT58M42S", "PT0S"), ("PT7H12M21S", "PT0S"), ("PT4H31M53S", "PT0S")]);

        Assert.NotNull(share);
        Assert.Equal(0, share.OnBatteryPct);
        Assert.Equal(12.7, share.HoursInUse);
    }

    [Fact]
    public void Mixed_use_is_reported_as_the_share_on_battery() =>
        Assert.Equal(75, BatteryUsage.Summarize([("PT1H", "PT3H")])!.OnBatteryPct);

    [Fact]
    public void No_usable_history_reports_nothing_rather_than_zero()
    {
        Assert.Null(BatteryUsage.Summarize([]));
        Assert.Null(BatteryUsage.Summarize([("not a duration", null), ("PT0S", "PT0S")]));
    }

    // --- Lifetime interface (CRC) errors on a scan with no history --------------------------

    [Theory]
    [InlineData(0, Grade.Good)]
    [InlineData(25, Grade.Normal)]   // the T470p's SSD: a lifetime total, nothing showing it is rising
    [InlineData(5000, Grade.Bad)]    // this many points at a problem still happening
    public void A_lifetime_crc_count_alone_is_not_an_emergency(double count, Grade expected) =>
        Assert.Equal(expected, Engine.EvaluateOne(new Metric("smart.crc_errors", "crc", count, HealthCategory.Storage)).Grade);
    // Growth between scans is still graded Bad by TrendAnalyzer (TrendHistoryTests).

    // --- SMART: the drive's own failure thresholds (vendor-neutral) --------------------------

    private static byte[] ThresholdTable(params (byte Id, byte Threshold)[] entries)
    {
        var table = new byte[512];
        for (var i = 0; i < entries.Length; i++)
        {
            table[2 + (i * 12)] = entries[i].Id;
            table[2 + (i * 12) + 1] = entries[i].Threshold;
        }
        return table;
    }

    [Fact]
    public void Threshold_table_is_parsed()
    {
        // The T470p's Intel SSD: 170 and 232 at 10, 184 at 97, the rest 0.
        var thresholds = SmartAttributeParser.ParseThresholds(ThresholdTable((5, 0), (170, 10), (184, 97), (232, 10)));

        Assert.Equal(10, thresholds[170]);
        Assert.Equal(97, thresholds[184]);
        Assert.Equal(0, thresholds[5]);
    }

    private static readonly Dictionary<int, int> IntelThresholds = new() { [5] = 0, [170] = 10, [184] = 97, [232] = 10 };

    [Fact]
    public void A_healthy_drive_has_nothing_at_its_limit()
    {
        // The T470p's real values: 170 = 100, 184 = 100, 232 = 100, and 5 at 100 with threshold 0.
        var (now, past) = SmartAttributeParser.AtThreshold(
            [new(5, "r", 100, 100, 0), new(170, "s", 100, 100, 0), new(184, "e", 100, 100, 0), new(232, "a", 100, 100, 0)],
            IntelThresholds);

        Assert.Empty(now);
        Assert.Empty(past);
    }

    [Fact]
    public void Spare_blocks_at_the_limit_are_failed_now_and_a_recovered_one_is_history()
    {
        var (now, past) = SmartAttributeParser.AtThreshold(
            [new(170, "spare", 9, 9, 0), new(184, "e2e", 100, 90, 0)],
            IntelThresholds);

        Assert.Equal(170, Assert.Single(now).Id);
        Assert.Equal(184, Assert.Single(past).Id);  // worst 90 <= 97, current 100 above it
    }

    [Fact]
    public void A_zero_threshold_never_fails() =>
        Assert.Empty(SmartAttributeParser.AtThreshold([new(5, "r", 0, 0, 0)], IntelThresholds).Now);

    [Theory]
    [InlineData(0, Grade.Good)]
    [InlineData(1, Grade.Bad)]
    public void Any_attribute_at_its_limit_is_bad(double count, Grade expected) =>
        Assert.Equal(expected, Engine.EvaluateOne(
            new Metric("smart.attributes_at_threshold", "limit", count, HealthCategory.Storage)).Grade);

    // --- 4. Which drive the disk errors came from -------------------------------------------

    [Theory]
    [InlineData(@"\Device\Harddisk2\DR2", 2)]
    [InlineData(@"\device\harddisk0\dr0", 0)]
    [InlineData("C:", null)]
    [InlineData(null, null)]
    public void The_disk_number_is_read_from_the_device_path(string? text, int? expected) =>
        Assert.Equal(expected, DiskEventAttribution.DiskNumber(text));

    [Fact]
    public void Errors_only_on_another_drive_say_the_system_disk_had_none()
    {
        // The T470p: two disk 153 events, both on the USB backup drive.
        var text = DiskEventAttribution.Describe([@"\Device\Harddisk2\DR2", @"\Device\Harddisk2\DR2"], systemDisk: 0);

        Assert.Contains("disk Windows runs from (disk 0): 0", text, StringComparison.Ordinal);
        Assert.Contains("disk 2: 2", text, StringComparison.Ordinal);
        Assert.Contains("no longer attached", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_without_a_disk_name_are_counted_separately()
    {
        var text = DiskEventAttribution.Describe([@"\Device\Harddisk0\DR0", "C:", null], systemDisk: 0);

        Assert.Contains("(disk 0): 1", text, StringComparison.Ordinal);
        Assert.Contains("no disk named: 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer attached", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_system_disk_is_not_guessed()
    {
        var text = DiskEventAttribution.Describe([@"\Device\Harddisk1\DR1"], systemDisk: null);

        Assert.Contains("by disk number: disk 1: 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows runs from", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_events_add_no_text() =>
        Assert.Equal("", DiskEventAttribution.Describe([], systemDisk: 0));
}
