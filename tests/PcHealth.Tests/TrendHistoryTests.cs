using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using Xunit;

namespace PcHealth.Tests;

public sealed class TrendHistoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pchealth-history-{Guid.NewGuid():N}.db");

    private HistoryStore NewStore() => new(_dbPath);

    private static Metric Number(string id, double value, string? subject = null, string label = "metric") =>
        new(id, label, value, HealthCategory.Storage, Subject: subject);

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public void Records_only_numeric_metrics_and_reads_them_back()
    {
        using var store = NewStore();
        var kept = store.RecordScan(DateTimeOffset.Now, [
            Number("smart.pending_sectors", 2, "Disk 0"),
            new Metric("integrity.image_health", "Store", "Healthy", HealthCategory.SoftwareOs),
            new Metric("security.tpm_ready", "TPM", true, HealthCategory.Security),
        ]);

        Assert.Equal(1, kept);
        var series = store.GetSeries("smart.pending_sectors", "Disk 0");
        Assert.Single(series);
        Assert.Equal(2, series[0].Value);
        Assert.Empty(store.GetSeries("integrity.image_health", null));
    }

    [Fact]
    public void Series_are_kept_per_subject_and_ordered_oldest_first()
    {
        using var store = NewStore();
        var start = DateTimeOffset.Now.AddDays(-10);
        store.RecordScan(start, [Number("smart.reallocated", 1, "Disk A"), Number("smart.reallocated", 9, "Disk B")]);
        store.RecordScan(start.AddDays(5), [Number("smart.reallocated", 3, "Disk A")]);

        var diskA = store.GetSeries("smart.reallocated", "Disk A");
        Assert.Equal([1d, 3d], diskA.Select(s => s.Value));
        Assert.True(diskA[0].TakenAt < diskA[1].TakenAt);
        Assert.Equal([9d], store.GetSeries("smart.reallocated", "Disk B").Select(s => s.Value));
    }

    [Fact]
    public void Coverage_reports_scan_count_and_span()
    {
        using var store = NewStore();
        var start = DateTimeOffset.Now.AddDays(-30);
        store.RecordScan(start, [Number("storage.wear_pct", 1)]);
        store.RecordScan(DateTimeOffset.Now, [Number("storage.wear_pct", 2)]);

        var coverage = store.GetCoverage();
        Assert.Equal(2, coverage.ScanCount);
        Assert.InRange(coverage.DaysCovered, 29, 31);
    }

    [Fact]
    public void Clear_removes_everything()
    {
        using var store = NewStore();
        store.RecordScan(DateTimeOffset.Now, [Number("smart.reallocated", 5, "Disk 0")]);
        store.Clear();

        Assert.Empty(store.GetSeries("smart.reallocated", "Disk 0"));
        Assert.Equal(0, store.GetCoverage().ScanCount);
    }

    [Fact]
    public void A_counter_rising_from_zero_is_the_loudest_finding()
    {
        using var store = NewStore();
        store.RecordScan(DateTimeOffset.Now.AddDays(-7), [Number("smart.pending_sectors", 0, "Disk 0", "Pending sectors")]);

        var findings = TrendAnalyzer.Analyze([Number("smart.pending_sectors", 2, "Disk 0", "Pending sectors")], store);

        var finding = Assert.Single(findings);
        Assert.Equal(TrendKind.CounterGrowth, finding.Kind);
        Assert.Equal(Grade.Bad, finding.Grade);
        Assert.Contains("new", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(finding.Tips, t => t.Contains("BACK UP", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stable_counter_produces_no_finding()
    {
        using var store = NewStore();
        store.RecordScan(DateTimeOffset.Now.AddDays(-30), [Number("smart.reallocated", 4, "Disk 0")]);

        Assert.Empty(TrendAnalyzer.Analyze([Number("smart.reallocated", 4, "Disk 0")], store));
    }

    [Fact]
    public void A_counter_that_grows_further_still_reports()
    {
        using var store = NewStore();
        store.RecordScan(DateTimeOffset.Now.AddDays(-3), [Number("smart.crc_errors", 6, "Disk 0", "CRC errors")]);

        var finding = Assert.Single(TrendAnalyzer.Analyze([Number("smart.crc_errors", 11, "Disk 0", "CRC errors")], store));
        Assert.Equal(Grade.Bad, finding.Grade);
        Assert.Equal(6, finding.PreviousValue);
        Assert.Equal(11, finding.CurrentValue);
        Assert.Contains(finding.Tips, t => t.Contains("cable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void First_ever_scan_yields_no_trends()
    {
        using var store = NewStore();
        Assert.Empty(TrendAnalyzer.Analyze([Number("smart.pending_sectors", 5, "Disk 0")], store));
    }

    [Fact]
    public void Ssd_wear_is_projected_to_an_end_of_life_date()
    {
        using var store = NewStore();
        // 5% of endurance consumed over 100 days => ~0.05%/day, so ~1,880 days (5 years)
        // remain before it reaches 100%. Comfortably beyond the two-year "watch" line.
        store.RecordScan(DateTimeOffset.Now.AddDays(-100), [Number("storage.wear_pct", 1, "SSD", "SSD endurance used")]);

        var finding = Assert.Single(TrendAnalyzer.Analyze([Number("storage.wear_pct", 6, "SSD", "SSD endurance used")], store));
        Assert.Equal(TrendKind.Projection, finding.Kind);
        Assert.Equal(Grade.Good, finding.Grade);
        Assert.Contains("a year", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wear_arriving_within_two_years_is_flagged_to_watch()
    {
        using var store = NewStore();
        // 20% over 100 days leaves ~350 days of rated life: worth planning for, not urgent.
        store.RecordScan(DateTimeOffset.Now.AddDays(-100), [Number("storage.wear_pct", 10, "SSD", "SSD endurance used")]);

        var finding = Assert.Single(TrendAnalyzer.Analyze([Number("storage.wear_pct", 30, "SSD", "SSD endurance used")], store));
        Assert.Equal(Grade.Normal, finding.Grade);
    }

    [Fact]
    public void Fast_wear_is_graded_as_needing_action()
    {
        using var store = NewStore();
        // 60% consumed in 60 days: hits 100% in well under six months.
        store.RecordScan(DateTimeOffset.Now.AddDays(-60), [Number("storage.wear_pct", 20, "SSD", "SSD endurance used")]);

        var finding = Assert.Single(TrendAnalyzer.Analyze([Number("storage.wear_pct", 80, "SSD", "SSD endurance used")], store));
        Assert.Equal(Grade.Bad, finding.Grade);
    }

    [Fact]
    public void Projections_need_a_meaningful_time_span()
    {
        using var store = NewStore();
        store.RecordScan(DateTimeOffset.Now.AddDays(-2), [Number("storage.wear_pct", 10, "SSD")]);

        // Two days of data cannot support a multi-year extrapolation.
        Assert.Empty(TrendAnalyzer.Analyze([Number("storage.wear_pct", 12, "SSD")], store));
    }

    [Fact]
    public void Wear_that_is_not_moving_is_not_projected()
    {
        using var store = NewStore();
        store.RecordScan(DateTimeOffset.Now.AddDays(-90), [Number("storage.wear_pct", 5, "SSD")]);

        Assert.Empty(TrendAnalyzer.Analyze([Number("storage.wear_pct", 5, "SSD")], store));
    }

    [Fact]
    public void A_trend_is_attributed_to_its_own_category_not_storage()
    {
        using var store = NewStore();
        var battery = new Metric("battery.capacity_pct", "Battery capacity", 70, HealthCategory.Battery);
        store.RecordScan(DateTimeOffset.Now.AddDays(-100), [battery with { Value = 90 }]);

        var finding = Assert.Single(TrendAnalyzer.Analyze([battery], store));
        // A wearing battery must not drag the storage score down with it.
        Assert.Equal(HealthCategory.Battery, finding.Category);
    }

    [Fact]
    public void History_is_capped_but_always_keeps_the_oldest_baseline()
    {
        using var store = NewStore();
        var start = DateTimeOffset.Now.AddYears(-3);

        // More scans than the cap; the oldest must survive because projections measure from it.
        for (var i = 0; i < 520; i++)
        {
            store.RecordScan(start.AddDays(i), [Number("storage.wear_pct", i * 0.1, "SSD")]);
        }

        var coverage = store.GetCoverage();
        Assert.InRange(coverage.ScanCount, 1, 501);
        var series = store.GetSeries("storage.wear_pct", "SSD");
        Assert.Equal(0d, series[0].Value);                       // the original baseline is still there
        Assert.Equal(start, series[0].TakenAt);
        Assert.Equal(51.9, Math.Round(series[^1].Value, 1));     // and the newest reading too
    }

    private static Metric Speed(double mbps) =>
        new("bench.disk.seq_read_mbps", "Sequential read speed", mbps, HealthCategory.Performance,
            Unit: "MB/s", Subject: "C: (CT1000P3PSSD8)");

    [Fact]
    public void A_drive_that_has_lost_a_third_of_its_speed_is_flagged_against_its_own_history()
    {
        // The whole reason for benchmarking: 2,000 MB/s means nothing on its own, but
        // 2,000 on a drive that has always done 3,000 means something on any drive.
        using var store = NewStore();
        var start = DateTimeOffset.Now.AddDays(-60);
        foreach (var (day, mbps) in new[] { (0, 3000d), (20, 3100d), (40, 2950d) })
        {
            store.RecordScan(start.AddDays(day), [Speed(mbps)]);
        }

        var finding = Assert.Single(TrendAnalyzer.Analyze([Speed(1900)], store));

        Assert.Equal(TrendKind.PerformanceDrop, finding.Kind);
        Assert.Equal(Grade.Normal, finding.Grade);
        Assert.Equal(HealthCategory.Performance, finding.Category);
        Assert.Contains("slower at reading long files", finding.Message, StringComparison.Ordinal);
        Assert.Contains("free space", finding.Tips[1], StringComparison.Ordinal);
        Assert.Contains("Re-run the check on an idle PC", finding.Tips[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Half_speed_is_treated_as_a_real_problem_rather_than_a_busy_moment()
    {
        using var store = NewStore();
        store.RecordScan(DateTimeOffset.Now.AddDays(-40), [Speed(3000)]);
        store.RecordScan(DateTimeOffset.Now.AddDays(-20), [Speed(3000)]);

        var finding = Assert.Single(TrendAnalyzer.Analyze([Speed(1200)], store));

        Assert.Equal(Grade.Bad, finding.Grade);
        Assert.Contains("BACK UP", finding.Tips[2], StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_slow_run_is_not_worth_alarming_anyone_about()
    {
        // A background update or antivirus scan easily costs 10-20%. Reporting that would
        // teach the user to ignore this section.
        using var store = NewStore();
        store.RecordScan(DateTimeOffset.Now.AddDays(-40), [Speed(3000)]);
        store.RecordScan(DateTimeOffset.Now.AddDays(-20), [Speed(3000)]);

        Assert.Empty(TrendAnalyzer.Analyze([Speed(2550)], store));
    }

    [Fact]
    public void One_earlier_reading_is_not_enough_to_call_something_a_regression()
    {
        // If that single run happened while the PC was busy, every later scan would report
        // a phantom improvement-turned-regression against it.
        using var store = NewStore();
        store.RecordScan(DateTimeOffset.Now.AddDays(-20), [Speed(3000)]);

        Assert.Empty(TrendAnalyzer.Analyze([Speed(1000)], store));
    }

    [Fact]
    public void The_usual_speed_is_the_median_so_one_bad_day_cannot_move_the_baseline()
    {
        using var store = NewStore();
        var start = DateTimeOffset.Now.AddDays(-60);
        // One catastrophic run among four good ones must not drag the baseline down far
        // enough to hide a genuine regression afterwards.
        foreach (var (day, mbps) in new[] { (0, 3000d), (15, 3000d), (30, 200d), (45, 3000d) })
        {
            store.RecordScan(start.AddDays(day), [Speed(mbps)]);
        }

        var finding = Assert.Single(TrendAnalyzer.Analyze([Speed(1500)], store));
        Assert.Contains("usual 3000", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slower_processor_gets_processor_advice_not_disk_advice()
    {
        // Telling someone to check their SATA cable because their CPU score fell would be
        // worse than saying nothing: it sends them looking in the wrong place.
        using var store = NewStore();
        var cpu = (double mips) => new Metric("bench.cpu.rating_mips", "Processor benchmark score", mips,
            HealthCategory.Performance, Unit: "MIPS", Subject: "Intel Core i5-12400F (6 cores / 12 threads)");
        store.RecordScan(DateTimeOffset.Now.AddDays(-40), [cpu(40000)]);
        store.RecordScan(DateTimeOffset.Now.AddDays(-20), [cpu(41000)]);

        // Median of 40000 and 41000 is 40500; 19000 is 47% of that, past the severe threshold.
        var finding = Assert.Single(TrendAnalyzer.Analyze([cpu(19000)], store));

        Assert.Equal(Grade.Bad, finding.Grade);
        Assert.Contains("slower at processing work", finding.Message, StringComparison.Ordinal);
        var advice = string.Join(" ", finding.Tips);
        Assert.Contains("Temperatures & fans", advice, StringComparison.Ordinal);
        Assert.Contains("too hot", advice, StringComparison.Ordinal);
        Assert.Contains("power mode", advice, StringComparison.Ordinal);
        Assert.DoesNotContain("SATA cable", advice, StringComparison.Ordinal);
        Assert.DoesNotContain("free space", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_analyzes_against_history_before_adding_the_current_scan()
    {
        // The ordering trap: recording first would make every counter look stable.
        HistoryService.AnalyzeAndRecord([Number("smart.pending_sectors", 0, "Disk 0", "Pending")], DateTimeOffset.Now.AddDays(-5), _dbPath);
        var outcome = HistoryService.AnalyzeAndRecord([Number("smart.pending_sectors", 3, "Disk 0", "Pending")], DateTimeOffset.Now, _dbPath);

        Assert.Single(outcome.Trends);
        Assert.Equal(Grade.Bad, outcome.Trends[0].Grade);
        Assert.Equal(2, outcome.Coverage.ScanCount);
        Assert.Equal(1, outcome.MeasurementsRecorded);
        Assert.Null(outcome.Error);
    }
}
