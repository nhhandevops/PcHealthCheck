using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Whether this Windows still gets security updates (OS-001, 2026-10-27). The first case is the
/// dev T470p as read that day: Windows 10 Pro 22H2, build 19045.6466, every ESU licence row
/// unlicensed. The app called it "supported until 2027-10-12". Each case is locked in both
/// directions: the PC that gets no updates must say so, and the one that does must not.
/// </summary>
public sealed class WindowsSupportTests
{
    private static readonly VerdictEngine Engine = new(RulesLoader.LoadEmbedded("windows"));
    private static readonly DateTime ScanDay = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Year1 = WindowsSupport.EsuYears[0].ActivationId;
    private static readonly Guid Year2 = WindowsSupport.EsuYears[1].ActivationId;
    private static readonly Guid Year3 = WindowsSupport.EsuYears[2].ActivationId;
    // An add-on row that is not one of Microsoft's published commercial years. Made up.
    private static readonly Guid OtherAddon = new("11111111-2222-3333-4444-555555555555");

    private static EsuLicence Row(Guid id, uint status = 0, bool key = false) => new(id, status, key);

    private static EsuLicence[] Rows(params EsuLicence[] licensed) =>
        [.. new[] { Year1, Year2, Year3, OtherAddon }.Select(id => licensed.FirstOrDefault(l => l.Id == id) ?? Row(id))];

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 12, 0, 0, DateTimeKind.Utc);

    private static Grade GradeOf(string id, MetricValue value) =>
        Engine.EvaluateOne(new Metric(id, id, value, HealthCategory.SoftwareOs)).Grade;

    // --- Windows 10 22H2: ESU licence, or a build only enrolled PCs get -------------------

    [Fact]
    public void An_unenrolled_windows_10_pro_gets_no_security_updates()
    {
        var verdict = WindowsSupport.Assess(19045, 6466, "Professional", Rows(), ScanDay);

        Assert.False(verdict.Supported);
        Assert.Equal("No Windows security updates since October 2025: no Extended Security Updates licence is active, " +
                     "and this build (19045.6466) has nothing newer.", verdict.Detail);
        Assert.Null(verdict.EndingSoon);
        Assert.Equal(Grade.Bad, GradeOf("os.supported", false));
    }

    [Fact]
    public void An_active_licence_for_the_year_that_covers_today_is_supported()
    {
        var verdict = WindowsSupport.Assess(19045, 6466, "Professional", Rows(Row(Year2, status: 1, key: true)), ScanDay);

        Assert.True(verdict.Supported);
        Assert.Contains("the Year 2 licence is active, to 2027-10-12", verdict.Detail, StringComparison.Ordinal);
        Assert.Null(verdict.EndingSoon); // 350 days left
        Assert.Equal(Grade.Good, GradeOf("os.supported", true));
    }

    [Fact]
    public void A_year_1_licence_alone_does_not_cover_today()
    {
        var verdict = WindowsSupport.Assess(19045, 6466, "Enterprise", Rows(Row(Year1, status: 1, key: true)), ScanDay);

        Assert.False(verdict.Supported);
        Assert.Contains("The Year 1 licence is active, but Year 1 ended on 2026-10-13; Year 2 is needed now.", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_installed_but_not_activated_is_named()
    {
        var verdict = WindowsSupport.Assess(19045, 6466, "Professional", Rows(Row(Year2, status: 0, key: true)), ScanDay);

        Assert.False(verdict.Supported);
        Assert.Contains("An ESU Year 2 key is installed but not activated (licence status 0): " +
                        "activate it with slmgr /ato 1043add5-23b1-4afb-9a0f-64343c8f3f8d.", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Licences_are_matched_by_microsofts_ids_not_by_any_licensed_add_on()
    {
        Assert.False(WindowsSupport.Assess(19045, 6466, "Professional", Rows(Row(OtherAddon, status: 1, key: true)), ScanDay).Supported);
    }

    [Theory]
    [InlineData(6466, false)]  // KB5071959, the last build a PC without ESU is offered
    [InlineData(6575, true)]   // KB5068781, the first ESU build
    [InlineData(7811, true)]
    public void A_build_newer_than_the_last_one_without_esu_shows_esu_updates_arrived(int ubr, bool supported)
    {
        var verdict = WindowsSupport.Assess(19045, ubr, "Professional", Rows(), ScanDay);

        Assert.Equal(supported, verdict.Supported);
        if (supported)
        {
            // Consumer ESU and a company ESU without a key look the same here, so neither is claimed.
            Assert.Contains("consumer ESU (personal use only, to 2027-10-12) or a company ESU that needs no key on the PC", verdict.Detail, StringComparison.Ordinal);
            Assert.Null(verdict.EndingSoon); // 350 days before consumer ESU ends
        }
    }

    // --- Review round, 2026-10-28: a licence outweighs a build, and a build stops counting ---

    private static readonly DateTime ReviewDay = Utc(2026, 10, 28);

    [Theory]
    [InlineData("Professional")]
    [InlineData("Enterprise")]
    public void A_year_1_licence_with_year_1_builds_does_not_cover_today(string edition)
    {
        // The realistic Year-1-only PC: it installed Year 1's updates, so its build is far above
        // 6466. That build is Year 1's doing and says nothing about Year 2. It read Good, "No
        // company ESU licence is active here", until 2028-10-10.
        var verdict = WindowsSupport.Assess(19045, 7811, edition, Rows(Row(Year1, status: 1, key: true)), ReviewDay);

        Assert.False(verdict.Supported);
        Assert.Contains("no active Extended Security Updates licence for Year 2, the year that covers today (to 2027-10-12)", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("The Year 1 licence is active, but Year 1 ended on 2026-10-13; Year 2 is needed now.", verdict.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("No company ESU licence is active", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_year_2_key_waiting_to_be_activated_is_named_on_a_pc_with_year_1_builds()
    {
        var verdict = WindowsSupport.Assess(19045, 7811, "Professional",
            Rows(Row(Year1, status: 1, key: true), Row(Year2, status: 5, key: true)), ReviewDay);

        Assert.False(verdict.Supported);
        Assert.Contains("An ESU Year 2 key is installed but not activated (licence status 5)", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Year_1_and_year_2_licences_with_year_1_builds_are_covered()
    {
        // The other direction: the company bought Year 2 as well.
        var verdict = WindowsSupport.Assess(19045, 7811, "Professional",
            Rows(Row(Year1, status: 1, key: true), Row(Year2, status: 1, key: true)), ReviewDay);

        Assert.True(verdict.Supported);
        Assert.Contains("the Year 2 licence is active, to 2027-10-12", verdict.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Professional")]
    [InlineData("Enterprise")]
    public void A_build_alone_stops_counting_when_consumer_esu_ends(string edition)
    {
        // The last day of consumer ESU still counts, with its six-month warning.
        var lastDay = WindowsSupport.Assess(19045, 7900, edition, Rows(), Utc(2027, 10, 12));
        Assert.True(lastDay.Supported);
        Assert.Contains("consumer ESU ends on 2027-10-12", lastDay.EndingSoon, StringComparison.Ordinal);
        Assert.Contains("If this is a company PC, activate its ESU licence for Year 2", lastDay.EndingSoon, StringComparison.Ordinal);
    }

    [Fact]
    public void After_consumer_esu_a_build_alone_is_bad_on_pro()
    {
        // A Pro PC on consumer ESU gets nothing after 2027-10-12, and its build still reads above
        // 6466. It read Good, "a company ESU delivered another way", until 2028-10-10.
        var after = WindowsSupport.Assess(19045, 7900, "Professional", Rows(), Utc(2027, 11, 1));
        Assert.False(after.Supported);
        Assert.Contains("consumer ESU ended on 2027-10-12, so nothing on this PC shows that security updates still arrive", after.Detail, StringComparison.Ordinal);
        Assert.Equal(Grade.Bad, GradeOf("os.supported", false));
    }

    [Fact]
    public void After_consumer_esu_a_build_alone_on_enterprise_is_not_judged_until_esu_ends_for_good()
    {
        // Enterprise never had consumer ESU, so its build means a keyless company ESU that runs to
        // 2028-10-10 and that this app cannot see: a Note, not Bad (review round, 2026-10-28).
        var during = WindowsSupport.Assess(19045, 7900, "Enterprise", Rows(), Utc(2027, 11, 1));
        Assert.Null(during.Supported);
        Assert.Contains("whether security updates still arrive was NOT established", during.Detail, StringComparison.Ordinal);

        // After Year 3 nothing can cover it any more.
        var after = WindowsSupport.Assess(19045, 7900, "Enterprise", Rows(), Utc(2028, 11, 1));
        Assert.False(after.Supported);
    }

    [Fact]
    public void The_warning_that_a_build_alone_stops_counting_comes_six_months_ahead()
    {
        Assert.NotNull(WindowsSupport.Assess(19045, 7900, "Professional", Rows(), Utc(2027, 6, 1)).EndingSoon);  // 133 days left
        Assert.Null(WindowsSupport.Assess(19045, 7900, "Professional", Rows(), ReviewDay).EndingSoon);         // 349 days left
    }

    [Theory]
    [InlineData("Enterprise")]
    [InlineData("Education")]
    [InlineData("ServerRdsh")]
    public void Enterprise_and_education_are_never_told_they_may_have_consumer_esu(string edition)
    {
        var verdict = WindowsSupport.Assess(19045, 7000, edition, Rows(), ReviewDay);

        Assert.True(verdict.Supported);
        Assert.DoesNotContain("consumer ESU (personal use only", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("consumer ESU is not offered for this edition, so this is a company ESU that needs no key on the PC, " +
                        "such as through Windows 365 or Azure Virtual Desktop", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Pro_with_esu_builds_may_still_be_consumer_esu()
    {
        var verdict = WindowsSupport.Assess(19045, 7000, "ProfessionalWorkstation", Rows(), ReviewDay);

        Assert.True(verdict.Supported);
        Assert.Contains("consumer ESU (personal use only, to 2027-10-12)", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Years_activated_in_a_row_are_not_an_ending()
    {
        // Year 3 already active: Year 2's end is no deadline, and the cover runs to Year 3's end.
        var both = WindowsSupport.Assess(19045, 7811, "Professional",
            Rows(Row(Year2, status: 1, key: true), Row(Year3, status: 1, key: true)), Utc(2027, 6, 1));
        Assert.True(both.Supported);
        Assert.Null(both.EndingSoon);
        Assert.Contains("the licences for Year 2 through Year 3 are active, to 2028-10-10", both.Detail, StringComparison.Ordinal);

        // A gap does not count: Year 3 alone during Year 2 is not cover.
        Assert.False(WindowsSupport.Assess(19045, 7811, "Professional", Rows(Row(Year3, status: 1, key: true)), Utc(2027, 6, 1)).Supported);
    }

    [Fact]
    public void Windows_10_team_has_no_esu_and_no_licence_read()
    {
        var verdict = WindowsSupport.Assess(19045, 6036, "PPIPro", [], ReviewDay);

        Assert.False(verdict.Supported);
        Assert.Contains("Windows 10 Team (Surface Hub) left support on 2025-10-14", verdict.Detail, StringComparison.Ordinal);
        Assert.False(WindowsSupport.NeedsEsuLicences(19045, "PPIPro", ReviewDay));
    }

    [Fact]
    public void Windows_10_home_says_what_home_rules_out()
    {
        var verdict = WindowsSupport.Assess(19045, 6466, "Core", [], ScanDay);

        Assert.False(verdict.Supported);
        Assert.Contains("company ESU covers Pro, Enterprise and Education only", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("BitLocker drive encryption needs Pro", verdict.Detail, StringComparison.Ordinal);
        Assert.False(WindowsSupport.NeedsEsuLicences(19045, "Core", ScanDay)); // Home has no company ESU rows to read
    }

    [Fact]
    public void Home_with_esu_builds_is_consumer_esu_until_its_end_and_not_after()
    {
        var during = WindowsSupport.Assess(19045, 6575, "CoreSingleLanguage", [], ScanDay);
        Assert.True(during.Supported);
        Assert.Contains("consumer ESU, which is for personal use only and ends 2027-10-12", during.Detail, StringComparison.Ordinal);

        var after = WindowsSupport.Assess(19045, 7811, "Core", [], Utc(2027, 11, 1));
        Assert.False(after.Supported);
        Assert.Contains("consumer Extended Security Updates ended on 2027-10-12", after.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clock_set_back_cannot_bring_windows_10_back_into_support()
    {
        Assert.Equal(WindowsSupport.CheckedOn, WindowsSupport.Today(Utc(2024, 1, 1)));
        Assert.False(WindowsSupport.Assess(19045, 6466, "Professional", Rows(Row(Year1, status: 1, key: true)), Utc(2024, 1, 1)).Supported);
    }

    [Fact]
    public void The_esu_year_ending_is_a_plan_to_make_six_months_ahead()
    {
        Assert.Contains("activate Year 3 before then",
            WindowsSupport.Assess(19045, 6466, "Professional", Rows(Row(Year2, status: 1, key: true)), Utc(2027, 6, 1)).EndingSoon,
            StringComparison.Ordinal);
        Assert.Contains("end for good on 2028-10-10",
            WindowsSupport.Assess(19045, 6466, "Professional", Rows(Row(Year3, status: 1, key: true)), Utc(2028, 6, 1)).EndingSoon,
            StringComparison.Ordinal);

        var over = WindowsSupport.Assess(19045, 9999, "Professional", Rows(Row(Year3, status: 1, key: true)), Utc(2028, 11, 1));
        Assert.False(over.Supported);
        Assert.Contains("ended on 2028-10-10", over.Detail, StringComparison.Ordinal);
    }

    // --- LTSC, Windows 11, and what the table does not know --------------------------------

    [Theory]
    [InlineData(19044, "EnterpriseS", true, true)]        // LTSC 2021 ends 2027-01-12, 77 days after the scan
    [InlineData(19044, "IoTEnterpriseS", true, false)]    // IoT LTSC 2021 runs to 2032
    [InlineData(17763, "EnterpriseS", true, false)]       // LTSC 2019 runs to 2029
    [InlineData(14393, "EnterpriseS", false, false)]      // LTSB 2016 ended 2026-10-13
    [InlineData(19044, "Professional", false, false)]     // 21H2, not LTSC: older than 22H2
    [InlineData(26100, "Professional", false, false)]     // Windows 11 24H2 Home and Pro ended 2026-10-13
    [InlineData(26100, "Enterprise", true, false)]        // 24H2 Enterprise runs a year longer
    [InlineData(26100, "EnterpriseS", true, false)]       // LTSC 2024 shares 24H2's build
    [InlineData(22631, "Education", true, true)]          // 23H2 Enterprise and Education end 2026-11-10
    [InlineData(22631, "Professional", false, false)]
    [InlineData(26200, "Core", true, false)]              // 25H2 to 2027-10-12
    [InlineData(26300, "Professional", true, false)]      // 26H2 to 2028-10-10
    [InlineData(10240, "EnterpriseS", false, false)]      // LTSB 2015 ended 2025-10-14 (it was NOT graded before 2026-10-28)
    [InlineData(10240, "IoTEnterpriseS", false, false)]   // IoT LTSB 2015, the same day
    [InlineData(14393, "IoTEnterpriseS", false, false)]   // IoT LTSB 2016 ended 2026-10-13
    [InlineData(17763, "IoTEnterpriseS", true, false)]    // IoT LTSC 2019 runs to 2029
    [InlineData(19042, "PPIPro", false, false)]           // Windows 10 Team 2020 Update
    [InlineData(19045, "PPIPro", false, false)]           // Windows 10 Team 2022 Update: no ESU
    public void Each_release_uses_its_own_dates(int build, string edition, bool supported, bool endingSoon)
    {
        var verdict = WindowsSupport.Assess(build, 1, edition, [], ScanDay);

        Assert.Equal(supported, verdict.Supported);
        Assert.Equal(endingSoon, verdict.EndingSoon is not null);
    }

    [Theory]
    [InlineData(20348, "ServerStandard")]   // Windows Server 2022: not in the table
    [InlineData(26100, "CloudEdition")]     // Windows 11 SE
    [InlineData(27000, "Professional")]     // a build newer than the table
    public void A_windows_the_table_does_not_know_is_not_graded(int build, string edition)
    {
        var verdict = WindowsSupport.Assess(build, 1, edition, [], ScanDay);

        Assert.Null(verdict.Supported);
        Assert.Contains("NOT graded", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_end_date_is_a_patch_tuesday()
    {
        // Lifecycle pages print the end as 6:59:59 AM on the NEXT day; reading that literally
        // gives a Wednesday. Every real end of updates is a second Tuesday.
        var dates = WindowsSupport.Table.Select(e => e.LastDay)
            .Concat(WindowsSupport.EsuYears.Select(y => y.LastDay))
            .Append(WindowsSupport.ConsumerEsuLastDay);
        Assert.All(dates, d => Assert.True(d.DayOfWeek == DayOfWeek.Tuesday && d.Day is >= 8 and <= 14, $"{d} is not a Patch Tuesday"));
        Assert.All(WindowsSupport.Table, e => Assert.StartsWith("https://", e.Source, StringComparison.Ordinal));
    }

    // --- The metrics, and a read that breaks ----------------------------------------------

    private static (List<Metric> Metrics, List<string> Notes, ModuleFailures Failures) Run(
        int build, Func<(string, int)> readRegistry, Func<IReadOnlyList<EsuLicence>> readEsu)
    {
        var metrics = new List<Metric>();
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);
        WindowsSupport.AddMetrics(metrics, failures, build, readRegistry, readEsu, ScanDay);
        return (metrics, notes, failures);
    }

    private static IReadOnlyList<EsuLicence> Unreadable() => throw new UnauthorizedAccessException("Access denied");

    [Fact]
    public void A_licence_read_that_breaks_is_a_failure_and_no_verdict()
    {
        var (metrics, notes, failures) = Run(19045, () => ("Professional", 6466), Unreadable);

        Assert.Equal(1, failures.Count);
        Assert.DoesNotContain(metrics, m => m.Id == "os.supported");
        Assert.Contains(notes, n => n.Contains("NOT checked", StringComparison.Ordinal));
    }

    [Fact]
    public void A_registry_read_that_breaks_is_a_failure_and_no_verdict()
    {
        var (metrics, _, failures) = Run(19045, () => throw new InvalidDataException("no UBR"), () => []);

        Assert.Equal(1, failures.Count);
        Assert.Empty(metrics);
    }

    [Fact]
    public void Licences_are_not_read_where_they_do_not_matter()
    {
        // Windows 11 and Home have no licence to read: a broken licence API there costs nothing.
        var (metrics, _, failures) = Run(26200, () => ("Professional", 6899), Unreadable);
        Assert.Equal(0, failures.Count);
        Assert.True(Assert.Single(metrics, m => m.Id == "os.supported").Value.Flag);

        var home = Run(19045, () => ("Core", 6466), Unreadable);
        Assert.Equal(0, home.Failures.Count);
        Assert.False(Assert.Single(home.Metrics, m => m.Id == "os.supported").Value.Flag);
    }

    [Fact]
    public void Home_is_named_as_information_and_pro_is_not()
    {
        var home = Run(26200, () => ("Core", 6899), () => []).Metrics;
        var info = Assert.Single(home, m => m.Id == "os.home_edition");
        Assert.True(info.InfoOnly);
        Assert.Contains("BitLocker", info.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(Run(26200, () => ("Professional", 6899), () => []).Metrics, m => m.Id == "os.home_edition");
    }

    [Fact]
    public void An_unknown_windows_is_a_note_not_a_failure()
    {
        var (metrics, notes, failures) = Run(20348, () => ("ServerStandard", 1), () => []);

        Assert.Equal(0, failures.Count);
        Assert.Empty(metrics);
        Assert.Single(notes);
    }

    [Fact]
    public void Ending_soon_is_its_own_normal_row()
    {
        var metrics = Run(22631, () => ("Enterprise", 6000), () => []).Metrics;

        Assert.True(Assert.Single(metrics, m => m.Id == "os.supported").Value.Flag);
        Assert.Contains("2026-11-10", Assert.Single(metrics, m => m.Id == "os.support_ending").Detail, StringComparison.Ordinal);
        Assert.Equal(Grade.Normal, GradeOf("os.support_ending", true));
    }

    [Fact]
    public void Windows_11_on_unsupported_hardware_is_normal_not_bad()
    {
        Assert.Equal(Grade.Normal, GradeOf("os.win11_hardware_supported", false));
        Assert.Equal(Grade.Good, GradeOf("os.win11_hardware_supported", true));
    }

    // --- The Windows licence is never an ESU add-on ----------------------------------------

    private static Dictionary<string, object?> Product(string name, bool addon, string? key) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Name"] = name, ["LicenseIsAddon"] = addon, ["PartialProductKey"] = key };

    [Fact]
    public void An_installed_esu_key_is_not_taken_for_the_windows_licence()
    {
        var esuFirst = new[] { Product("ESU Year 2 add-on", true, "AAAAA"), Product("Windows Professional", false, "BBBBB") };
        Assert.Equal("Windows Professional", WindowsSupport.WindowsLicence(esuFirst)?["Name"]);

        Assert.Null(WindowsSupport.WindowsLicence([Product("ESU Year 2 add-on", true, "AAAAA"), Product("Windows Professional", false, null)]));
    }
}
