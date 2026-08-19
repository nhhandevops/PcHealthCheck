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
