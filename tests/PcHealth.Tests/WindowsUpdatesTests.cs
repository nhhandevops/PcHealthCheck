using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// How current Windows' own updates are (OS-001, 2026-10-27). On the dev T470p "days since last
/// update" read Good because Defender definitions install daily, while Windows itself had had no
/// security fix since October 2025; and its 13 updates waiting were 12 driver offers and a
/// firmware update. Each case is locked in both directions.
/// </summary>
public sealed class WindowsUpdatesTests
{
    private static readonly VerdictEngine Engine = new(RulesLoader.LoadEmbedded("windows"));
    private static readonly DateTime ScanDay = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private const string SecurityUpdates = "0fa1201d-4330-4fa8-8ae9-b877473b6441";
    private const string Drivers = "ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0";

    private static PendingUpdate Driver(string driverClass) => new(true, driverClass, false, [Drivers]);
    private static PendingUpdate Software(string classification, bool browseOnly = false) => new(false, null, browseOnly, [classification]);

    private static Grade GradeOf(string id, double value) =>
        Engine.EvaluateOne(new Metric(id, id, value, HealthCategory.SoftwareOs)).Grade;

    private static (List<Metric> Metrics, List<string> Notes, ModuleFailures Failures) Pending(
        PendingCounts counts, DateTime? lastSearch, bool searchDateRead = true)
    {
        var metrics = new List<Metric>();
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);
        WindowsUpdates.AddPendingMetrics(metrics, failures, counts, lastSearch, searchDateRead, ScanDay);
        return (metrics, notes, failures);
    }

    // --- What is waiting, by kind ---------------------------------------------------------

    [Fact]
    public void The_dev_t470ps_thirteen_waiting_were_drivers_and_firmware_not_security_updates()
    {
        var offers = Enumerable.Repeat(Driver("OtherHardware"), 11).Append(Driver("Networking")).Append(Driver("Firmware"));

        Assert.Equal(new PendingCounts(0, 12, 1, 0, 0), WindowsUpdates.Count(offers));
    }

    [Fact]
    public void Security_updates_count_and_definitions_and_optional_updates_do_not()
    {
        var counts = WindowsUpdates.Count([
            Software(SecurityUpdates),
            Software(WindowsUpdates.DefinitionUpdates.ToUpperInvariant()),
            Software("cd5ffd1e-e932-4e3a-bf74-18bf0b1bbd83", browseOnly: true),
        ]);

        Assert.Equal(new PendingCounts(1, 0, 0, 1, 1), counts);
    }

    [Theory]
    [InlineData(-1.0, true)]
    [InlineData(-6.9, true)]
    [InlineData(-8.0, false)]   // a week and a day: the zero may be stale
    [InlineData(2.0, false)]    // dated after the clock: one of the two is wrong
    public void A_search_is_fresh_for_a_week(double daysFromScan, bool fresh) =>
        Assert.Equal(fresh, WindowsUpdates.SearchIsFresh(ScanDay.AddDays(daysFromScan), ScanDay));

    [Fact]
    public void A_zero_from_a_recent_search_is_graded_and_the_offers_are_information()
    {
        var (metrics, _, failures) = Pending(new PendingCounts(0, 12, 1, 0, 0), ScanDay.AddHours(-11));

        var pending = Assert.Single(metrics, m => m.Id == "updates.pending_count");
        Assert.Equal(0, pending.Value.Number);
        Assert.Contains("12 driver offers, 1 firmware offer", pending.Detail, StringComparison.Ordinal);
        Assert.Equal(Grade.Good, Engine.EvaluateOne(pending).Grade);
        Assert.True(Assert.Single(metrics, m => m.Id == "updates.pending_drivers").InfoOnly);
        Assert.True(Assert.Single(metrics, m => m.Id == "updates.pending_firmware").InfoOnly);
        Assert.Equal(0, failures.Count);
    }

    [Fact]
    public void A_zero_from_a_stale_search_is_a_note_not_a_grade()
    {
        var (metrics, notes, failures) = Pending(new PendingCounts(0, 0, 0, 0, 0), ScanDay.AddDays(-20));

        Assert.DoesNotContain(metrics, m => m.Id == "updates.pending_count");
        Assert.Equal("Windows Update has not searched since 2026-10-07, so its cached count of 0 updates waiting was NOT graded.", Assert.Single(notes));
        Assert.Equal(0, failures.Count); // nothing broke: Windows Update simply has not looked
    }

    [Fact]
    public void A_zero_from_no_search_at_all_is_a_note()
    {
        var (metrics, notes, _) = Pending(new PendingCounts(0, 0, 0, 0, 0), null);

        Assert.Empty(metrics);
        Assert.Contains("never searched", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_count_from_a_stale_search_is_still_graded_as_a_lower_bound()
    {
        var (metrics, _, _) = Pending(new PendingCounts(3, 0, 0, 0, 0), ScanDay.AddDays(-30));

        var pending = Assert.Single(metrics, m => m.Id == "updates.pending_count");
        Assert.Equal(3, pending.Value.Number);
        Assert.StartsWith("At least 3:", pending.Detail, StringComparison.Ordinal);
        Assert.Equal(Grade.Normal, Engine.EvaluateOne(pending).Grade);
    }

    [Fact]
    public void An_unreadable_search_date_adds_no_note_of_its_own()
    {
        // The caller recorded the failure; a second line saying "never searched" would be false.
        var (metrics, notes, _) = Pending(new PendingCounts(0, 0, 0, 0, 0), null, searchDateRead: false);

        Assert.Empty(metrics);
        Assert.Empty(notes);
    }

    // --- The newest Windows update, by its release ----------------------------------------

    [Theory]
    [InlineData("Package_for_RollupFix~31bf3856ad364e35~amd64~~19041.6466.1.0", 6466)]
    [InlineData("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4652.1.12", 4652)]
    [InlineData("Package_for_KB5001716~31bf3856ad364e35~amd64~~19041.6466.1.0", null)]
    [InlineData("Microsoft-Windows-Client-Features-Package~31bf3856ad364e35~amd64~~10.0.19041.6466", null)]
    public void The_cumulative_update_package_names_its_update_number(string name, int? ubr) =>
        Assert.Equal(ubr, WindowsUpdates.RollupUbr(name));

    [Fact]
    public void The_package_manifest_names_its_kb()
    {
        // The start of the dev T470p's RollupFix manifest, as Microsoft ships it.
        const string start = """
            <?xml version="1.0" encoding="utf-8"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v3" manifestVersion="1.0" description="Fix for KB5071959" displayName="default">
              <assemblyIdentity name="Package_for_RollupFix" version="19041.6466.1.0" processorArchitecture="amd64" language="neutral" />
              <package identifier="KB5071959" releaseType="Security Update" restart="possible">
            """;

        Assert.Equal("KB5071959", WindowsUpdates.ManifestKb(start));
        Assert.Null(WindowsUpdates.ManifestKb("<assembly><assemblyIdentity name=\"x\" /></assembly>"));
    }

    private static (List<Metric> Metrics, List<string> Notes, ModuleFailures Failures) Age(string? kb, DateTime? released, bool fromUpdateServer = false)
    {
        var metrics = new List<Metric>();
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);
        WindowsUpdates.AddAgeMetrics(metrics, failures, "19045.6466", kb, released, fromUpdateServer, ScanDay);
        return (metrics, notes, failures);
    }

    [Fact]
    public void A_date_from_a_company_update_server_is_not_graded()
    {
        // On WSUS the cached date is the day the server approved the update. The October update
        // approved in December read 36 days old (Good) in January, when it was 99 (Bad).
        var (metrics, notes, failures) = Age("KB5126657", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), fromUpdateServer: true);

        Assert.DoesNotContain(metrics, m => m.Id == "os.patch.age_days");
        Assert.Equal("KB5126657 (build 19045.6466)", Assert.Single(metrics, m => m.Id == "updates.newest_windows_update").Value.Text);
        var note = Assert.Single(notes);
        Assert.Contains("company update server", note, StringComparison.Ordinal);
        Assert.Contains("NOT graded", note, StringComparison.Ordinal);
        Assert.Equal(0, failures.Count); // a choice not to grade, not a check that broke

        // The same date from Microsoft's own Windows Update is graded.
        Assert.Contains(Age("KB5126657", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)).Metrics, m => m.Id == "os.patch.age_days");
    }

    [Fact]
    public void Of_several_cached_dates_for_one_kb_the_earliest_counts()
    {
        var released = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var revised = new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc);

        var dates = WindowsUpdates.ReleaseDates([new(["KB5126657"], revised), new(["KB5126657", "KB5000001"], released)]);

        Assert.Equal(released, dates["kb5126657"]);
        Assert.Equal(released, dates["KB5000001"]);
    }

    [Fact]
    public void The_age_is_counted_from_the_release_not_the_install()
    {
        // KB5071959 was installed on the dev T470p eight days before the scan; it came out on 2025-11-11.
        var (metrics, _, _) = Age("KB5071959", new DateTime(2025, 11, 11, 0, 0, 0, DateTimeKind.Utc));

        var age = Assert.Single(metrics, m => m.Id == "os.patch.age_days");
        Assert.Equal(350, age.Value.Number);
        Assert.Equal(Grade.Bad, Engine.EvaluateOne(age).Grade);
        Assert.Equal("KB5071959 (build 19045.6466)", Assert.Single(metrics, m => m.Id == "updates.newest_windows_update").Value.Text);
    }

    [Fact]
    public void A_pc_on_this_months_update_reads_good()
    {
        var (metrics, _, _) = Age("KB5126657", new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(Grade.Good, Engine.EvaluateOne(Assert.Single(metrics, m => m.Id == "os.patch.age_days")).Grade);
    }

    [Theory]
    [InlineData(42, Grade.Good)]    // a five-week Patch Tuesday gap plus a week to install
    [InlineData(43, Grade.Normal)]
    [InlineData(70, Grade.Normal)]  // one monthly update missed
    [InlineData(71, Grade.Bad)]     // two missed
    public void A_healthy_monthly_cadence_never_trips_the_age_bands(double days, Grade expected) =>
        Assert.Equal(expected, GradeOf("os.patch.age_days", days));

    [Fact]
    public void Without_a_package_or_a_release_date_the_age_is_a_note()
    {
        var noPackage = Age(null, null);
        Assert.DoesNotContain(noPackage.Metrics, m => m.Id == "os.patch.age_days");
        Assert.Equal("build 19045.6466", Assert.Single(noPackage.Metrics).Value.Text);
        Assert.Contains("NOT checked", Assert.Single(noPackage.Notes), StringComparison.Ordinal);
        Assert.Equal(0, noPackage.Failures.Count);

        var notInCache = Age("KB5071959", null);
        Assert.DoesNotContain(notInCache.Metrics, m => m.Id == "os.patch.age_days");
        Assert.Contains("does not list KB5071959", Assert.Single(notInCache.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_after_the_clock_is_not_graded()
    {
        var (metrics, notes, _) = Age("KB5999999", new DateTime(2026, 12, 8, 0, 0, 0, DateTimeKind.Utc));

        Assert.DoesNotContain(metrics, m => m.Id == "os.patch.age_days");
        Assert.Contains("after this PC's clock", Assert.Single(notes), StringComparison.Ordinal);
    }
}
