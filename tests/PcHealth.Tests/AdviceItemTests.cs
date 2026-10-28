using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcHealth.Core.Advice;
using PcHealth.Core.Advice.Items;
using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Reporting;
using PcHealth.Core.Text;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// A report as the advice reads it, built for a test. Readings are graded by the real Windows rule
/// files, so "3 retired areas means check again" fails here the day the rule's band moves, and the
/// modules are the real Windows modules, with their ids and names, every one Completed to start
/// with: a scan that finished. A reading added with the id, subject and disk of one already here
/// replaces it.
/// </summary>
internal sealed class AdvicePc
{
    public const string Ssd = "MODEL-A — 240 GB", DataDisk = "MODEL-C — 1000 GB", Stick = "STICK-B — 64 GB", Cpu = "CPU-A";

    /// <summary>When an earlier scan saw a value that has since moved.</summary>
    public static readonly DateTimeOffset LastScan = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every trend's Message. A card must never quote one (STEP-B-SPEC invariant 5).</summary>
    public const string TrendMessage = "Getting worse: 9,0 at the last scan";

    public static readonly VerdictEngine Rules = new(RulesLoader.LoadEmbedded("windows"));

    public static readonly IReadOnlyList<ModuleManifest> Manifests = [.. new WindowsModuleProvider().CreateModules().Select(m => m.Manifest)];

    /// <summary>
    /// The readings only the administrator check makes, by id or prefix: what the modules that need
    /// admin rights write (disk.smart, disk.reliability, backup.posture, security.platform,
    /// integrity.quick, sensors, startup.allusers).
    /// </summary>
    private static readonly string[] AdminReadings =
    [
        "smart.", "storage.wear_pct", "storage.temperature_c", "storage.read_errors_uncorrected", "storage.write_errors_uncorrected",
        "storage.power_on_hours", "storage.volume_clean", "cpu.temperature_c", "cpu.zone_temperature_c", "gpu.", "fans.",
        "memory.used_pct", "integrity.", "security.tpm_", "security.secure_boot", "security.vbs_running", "security.drive_encrypted",
        "backup.restore_point", "backup.shadow_copies", "startup.other_accounts",
    ];

    private readonly List<Verdict> _verdicts = [];
    private readonly List<TrendFinding> _trends = [];
    private readonly Dictionary<string, ModuleStatus> _modules =
        Manifests.ToDictionary(m => m.Id, _ => ModuleStatus.Completed, StringComparer.Ordinal);

    public IReadOnlyList<Verdict> Verdicts => _verdicts;

    public IReadOnlyList<TrendFinding> Trends => _trends;

    public IReadOnlyList<ModuleRunSummary> Modules =>
        [.. _modules.Select(m => new ModuleRunSummary(m.Key, NameOf(m.Key), m.Value, TimeSpan.Zero, null, []))];

    public static string NameOf(string moduleId) => Manifests.FirstOrDefault(m => m.Id == moduleId)?.Name ?? moduleId;

    public AdvicePc Read(string id, MetricValue value, string? subject = null, int? disk = null, string? detail = null) =>
        Add(new Metric(id, id, value, HealthCategory.Storage, Subject: subject, Detail: detail, DiskNumber: disk));

    public AdvicePc Info(string id, MetricValue value, string? subject = null, int? disk = null) =>
        Add(new Metric(id, id, value, HealthCategory.Storage, Subject: subject, InfoOnly: true, DiskNumber: disk));

    /// <summary>A drive-health reading, on the laptop's own SSD unless told otherwise.</summary>
    public AdvicePc Drive(string id, double value, int? disk = 0, string subject = Ssd) => Read(id, value, subject, disk);

    /// <summary>The volume check's two rows about a disk: where it is, and the space in use on it when measured.</summary>
    public AdvicePc Disk(int number, string role, string letters, double? usedGb = null)
    {
        Info(AdviceFacts.DiskRoleId, role, letters, number);
        return usedGb is { } gb ? Info(AdviceFacts.DiskUsedGbId, gb, letters, number) : this;
    }

    /// <summary>A counter that grew since <see cref="LastScan"/>. TrendAnalyzer grades every one Bad.</summary>
    public AdvicePc Growth(string id, double from, double to, string subject = Ssd) =>
        Trend(new TrendFinding(TrendKind.CounterGrowth, id, subject, id, Grade.Bad, TrendMessage, from, to, LastScan, []));

    public AdvicePc Projection(string id, double from, double to, Grade grade, string subject = Ssd) =>
        Trend(new TrendFinding(TrendKind.Projection, id, subject, id, grade, TrendMessage, from, to, LastScan, []));

    /// <summary>Null takes the module out of the scan, as when it was not selected or not run.</summary>
    public AdvicePc Module(string id, ModuleStatus? status)
    {
        if (status is { } s) _modules[id] = s;
        else _modules.Remove(id);
        return this;
    }

    public AdvicePc Without(string id)
    {
        _verdicts.RemoveAll(v => v.Metric.Id == id);
        return this;
    }

    /// <summary>A standard user's scan: no module that needs admin rights ran, so none of their readings exist.</summary>
    public AdvicePc Unelevated()
    {
        foreach (var manifest in Manifests.Where(m => m.NeedsAdmin)) _modules.Remove(manifest.Id);
        _verdicts.RemoveAll(v => IsAdminReading(v.Metric.Id));
        _trends.RemoveAll(t => IsAdminReading(t.MetricId));
        return this;
    }

    /// <summary>Gives every reading this Detail, as a module's own detail line can carry a folder path.</summary>
    public AdvicePc DetailOnEveryReading(string detail)
    {
        var metrics = _verdicts.Select(v => v.Metric with { Detail = detail }).ToList();
        _verdicts.Clear();
        foreach (var metric in metrics) Add(metric);
        return this;
    }

    public AdviceSection Advice(string? systemDrive = "C:", bool ranElevated = false) =>
        AdviceEngine.ForOs("windows").Derive(_verdicts, _trends, Modules, new AdviceContext(systemDrive, ranElevated))!;

    /// <summary>
    /// The whole report, through the orchestrator as a real scan builds it. Its advice uses this
    /// PC's own Windows drive, so a test reading it must not depend on which letter that is.
    /// </summary>
    public ScanReport Report(bool ranElevated)
    {
        var results = _modules.Select((m, i) => new ModuleResult
        {
            ModuleId = m.Key,
            Status = m.Value,
            Metrics = i == 0 ? [.. _verdicts.Select(v => v.Metric)] : [],
        });
        return new ScanOrchestrator(Rules).BuildReport(results, [], ranElevated,
            Manifests.ToDictionary(m => m.Id, m => m.Name), _trends);
    }

    /// <summary>
    /// A healthy PC that finished every check, generated from the rule files: for each Windows rule's
    /// first Good band, the value at its edge (eq and anyOf take the value, lte and gte the bound, lt
    /// and gt one step inside it). Plus the information-only rows a real scan writes beside them.
    /// </summary>
    public static AdvicePc AllGood()
    {
        var pc = new AdvicePc();
        foreach (var rule in RulesLoader.LoadEmbedded("windows"))
        {
            // os.support_ending has no Good band: it is written only when support is ending.
            if (rule.Bands.FirstOrDefault(b => string.Equals(b.Grade, "Good", StringComparison.OrdinalIgnoreCase)) is not { } good) continue;
            var (subject, disk) = WhereReadingIs(rule.MetricId);
            pc.Read(rule.MetricId, GoodValue(good), subject, disk);
        }
        return pc.Disk(0, "system", "C:", usedGb: 100)
            .Info("ram.total_gb", 15.9)
            .Info("os.win11_blockers", "none")
            .Info("events.cpu_speed_limited_30d", 3)
            .Info("backup.onedrive_folders", false)
            .Projection("storage.wear_pct", 10, 12, Grade.Good);
    }

    /// <summary>
    /// A laptop just restored from the image, Windows 10 22H2 without Extended Security Updates, read
    /// by the kit with its USB stick plugged in: everything a fresh install lacks is graded, and none
    /// of it is advice. The stick's read errors are Bad and on a disk the volume check calls external.
    /// </summary>
    public static AdvicePc FreshReimage(string blockers = "CpuFms", double uptimeDays = 0.2) =>
        AllGood()
            .Without("os.win11_hardware_supported") // Windows 10 has the readiness row instead
            .Read("os.uptime_days", uptimeDays)
            .Read("os.pending_reboot", true)
            .Read("updates.pending_count", 14)
            .Read("os.supported", false)
            .Read("security.defender.sig_age_days", 30)
            .Read("security.drive_encrypted", false, "C:")
            .Read("backup.file_history", false)
            .Read("backup.any_protection", false)
            .Read("backup.restore_points", 0)
            .Read("os.win11_ready", false)
            .Info("os.win11_blockers", blockers)
            .Read("storage.volume.free_pct", 70, "C:")
            .Info("storage.volume.free_pct", 5, "E:")
            .Disk(1, "external", "E:", 58.2)
            .Read("storage.read_errors_uncorrected", 3, Stick, 1);

    internal static MetricValue GoodValue(Band band)
    {
        if (band.Eq is { } eq) return ValueOf(eq);
        if (band.AnyOf is [var first, ..]) return ValueOf(first);
        double? edge = band.Lte ?? band.Gte ?? band.Lt - 1 ?? band.Gt + 1;
        return edge ?? throw new InvalidOperationException("A Good band with no value to stand on.");
    }

    private static MetricValue ValueOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => value.GetString()!,
        _ => throw new InvalidOperationException($"A band value of kind {value.ValueKind}."),
    };

    private static (string? Subject, int? Disk) WhereReadingIs(string metricId) => metricId switch
    {
        "storage.volume.free_pct" or "storage.volume.healthy" or "storage.volume_clean" or "storage.trim.enabled"
            or "security.drive_encrypted" => ("C:", null),
        "security.firewall.enabled" => ("Public profile", null),
        _ when metricId.StartsWith("smart.", StringComparison.Ordinal) || metricId.StartsWith("storage.", StringComparison.Ordinal) => (Ssd, 0),
        _ => (null, null),
    };

    private static bool IsAdminReading(string metricId) => AdminReadings.Any(p => metricId.StartsWith(p, StringComparison.Ordinal));

    private AdvicePc Add(Metric metric)
    {
        _verdicts.RemoveAll(v => v.Metric.Id == metric.Id && v.Metric.Subject == metric.Subject && v.Metric.DiskNumber == metric.DiskNumber);
        _verdicts.Add(Rules.EvaluateOne(metric));
        return this;
    }

    private AdvicePc Trend(TrendFinding trend)
    {
        _trends.Add(trend);
        return this;
    }
}

/// <summary>
/// The Step B items, each on both sides of every threshold it uses (STEP-B-SPEC 5 and 7). The
/// readings are graded by the real rule files, so the sides are the rules' own band edges.
/// </summary>
public sealed partial class AdviceItemTests
{
    private const string Ssd = AdvicePc.Ssd;

    private static Recommendation Only(AdviceSection section, string item)
    {
        Assert.Empty(section.ItemErrors);
        return Assert.Single(section.Recommendations, c => c.Item == item);
    }

    private static void Silent(AdviceSection section)
    {
        Assert.Empty(section.ItemErrors);
        Assert.Empty(section.Recommendations);
    }

    private static List<string> Lines(Recommendation card) => [.. card.Lines.Select(l => l.Text)];

    private static List<string> Keys(Recommendation card) => [.. card.Lines.Select(l => l.Key)];

    private static string AllText(Recommendation card) => string.Join("\n", Lines(card).Prepend(card.Title.Text));

    private static string Day(DateTimeOffset when) => when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The laptop's own SSD holds C:, with 104.4 GB in use.</summary>
    private static AdvicePc SystemSsd() => new AdvicePc().Disk(0, "system", "C:", 104.4);

    /// <summary>That SSD, with its error counters read and clean: a scan that read the drive and found nothing.</summary>
    private static AdvicePc DriveRead() => SystemSsd().Drive("storage.read_errors_uncorrected", 0);

    // --- H1: a failing or worn drive ----------------------------------------------------------------

    /// <summary>The readings that make a drive "failing", with what each one's line says about a value.</summary>
    public static TheoryData<string, object, string> DamageReadings => new()
    {
        { "smart.predict_failure", false, "Its own firmware predicts that it will fail." }, // the row is "not predicting failure"
        { "smart.attributes_at_threshold", 1, "1 of its health readings has reached the failure limit its maker set." },
        { "smart.reallocated", 12, "It has retired 12 damaged areas so far." },
        { "smart.pending_sectors", 1, "1 area is unstable, and the data in it may already be unreadable." },
        { "smart.offline_uncorrectable", 2, "Its own background check found 2 areas it could not recover." },
        { "smart.reported_uncorrectable", 7, "It has reported 7 errors it could not correct." },
        { "storage.read_errors_uncorrected", 1, "It failed to read data back 1 time." },
        { "storage.write_errors_uncorrected", 40, "It failed to write data 40 times." },
    };

    private static MetricValue ValueOf(object value) => value is bool flag ? flag : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    [Theory]
    [MemberData(nameof(DamageReadings))]
    public void Every_damage_reading_says_back_up_now_and_what_it_found(string metricId, object value, string line)
    {
        var card = Only(SystemSsd().Read(metricId, ValueOf(value), Ssd, 0).Advice(), "H1");

        Assert.Equal((AdviceUrgency.ActNow, 10, "drive-health", "H1:disk0", Ssd, (int?)0),
            (card.Urgency, card.Rank, card.Area, card.Id, card.Subject, card.DiskNumber));
        Assert.Equal("Back up now: the drive Windows runs from is failing", card.Title.Text);
        Assert.Contains(line, Lines(card));
        Assert.Contains(card.Evidence, e => e.MetricId == metricId && e.DiskNumber == 0);
    }

    [Fact]
    public void The_damage_readings_are_the_ones_the_disk_benchmark_refuses_to_write_to() =>
        Assert.Equal(DriveDamage.MetricIds.Order(StringComparer.Ordinal),
            DamageReadings.Select(row => (string)row[0]).Order(StringComparer.Ordinal));

    [Fact]
    public void The_pilot_laptop_s_card_reads_in_full()
    {
        // The pilot T470p: an internal SATA SSD with 5 retired sectors, holding C:.
        var card = Only(SystemSsd().Drive("smart.reallocated", 5).Advice(), "H1");

        Assert.Equal(Grade.Bad, card.Severity);
        Assert.Equal(
        [
            (AdviceLineRole.Finding, "This is the drive Windows runs from (C:)."),
            (AdviceLineRole.Finding, "It has retired 5 damaged areas so far."),
            (AdviceLineRole.Why, "Drives that start doing this fail far more often, some without further warning."),
            (AdviceLineRole.Do, "First, copy your files off to an external drive or OneDrive, then open a few of the copies to check they work."),
            (AdviceLineRole.Do, "About 104 GB is in use on C:, Windows and programs included. Copying all of it to a USB 3 drive takes " +
                                "about 20 to 40 minutes at a typical 50 to 100 MB/s; your own files are usually much less."),
            (AdviceLineRole.Finding, "Whether this account's files are backed up was not established in this scan."),
            (AdviceLineRole.Finding, "If nothing is done, everything on this drive may be lost."),
            (AdviceLineRole.DoNot, "Until your files are copied, do not run defrag, a full disk check (chkdsk /r), a speed test or " +
                                   "'repair' tools on it: they work the drive hard."),
            (AdviceLineRole.ForIt, "Replace it with a known-brand SSD of at least 256 GB (1.5 times the space in use, rounded up to a " +
                                   "standard size), with the same connection and physical size as the old one: check the PC's manual."),
            (AdviceLineRole.ForIt, "When the drive is retired: if BitLocker was on, delete its recovery key and wipe the drive; if not, " +
                                   "destroy it or use its maker's secure erase."),
        ], card.Lines.Select(l => (l.Role, l.Text)));
        var count = card.Lines.Single(l => l.Key == "advice.h1.evidence.smart.reallocated").Args["count"];
        Assert.Equal(new AdviceArg(5, null, AdviceUnit.Count), count);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, AdviceUrgency.CheckAgain)]
    [InlineData(3, AdviceUrgency.CheckAgain)]
    [InlineData(4, AdviceUrgency.CheckAgain)]
    [InlineData(5, AdviceUrgency.ActNow)]
    public void Retired_areas_check_again_from_one_and_act_now_from_five(double count, AdviceUrgency? expected)
    {
        var section = SystemSsd().Drive("smart.reallocated", count).Advice();

        if (expected is null) Silent(section);
        else Assert.Equal(expected, Only(section, "H1").Urgency);
    }

    [Fact]
    public void A_small_count_with_no_history_asks_to_check_again_and_does_not_say_replace()
    {
        var card = Only(SystemSsd().Drive("smart.reallocated", 3).Advice(), "H1");

        Assert.Equal((AdviceUrgency.CheckAgain, 12, Grade.Normal), (card.Urgency, card.Rank, card.Severity));
        Assert.Equal("Back up this drive, then scan again in 1-2 weeks", card.Title.Text);
        Assert.Contains("A small count that stays the same is usually fine; a rising one is not.", Lines(card));
        Assert.Contains("advice.h1.check-again.rescan", Keys(card));
        Assert.DoesNotContain(card.Lines, l => l.Role == AdviceLineRole.ForIt);
    }

    [Theory]
    [InlineData("smart.pending_sectors")]
    [InlineData("smart.offline_uncorrectable")]
    public void One_unstable_or_unrecoverable_area_is_act_now(string metricId) =>
        Assert.Equal(AdviceUrgency.ActNow, Only(SystemSsd().Drive(metricId, 1).Advice(), "H1").Urgency);

    [Fact]
    public void A_count_that_grew_lifts_check_again_to_act_now_and_says_by_how_much()
    {
        var pc = SystemSsd().Drive("smart.reallocated", 3);
        Assert.Equal(AdviceUrgency.CheckAgain, Only(pc.Advice(), "H1").Urgency);

        var card = Only(pc.Growth("smart.reallocated", 2, 3).Advice(), "H1");

        Assert.Equal(AdviceUrgency.ActNow, card.Urgency);
        var lines = Lines(card);
        var grown = lines.IndexOf($"The count has grown since the scan of {Day(AdvicePc.LastScan)}: 2 then, 3 now.");
        Assert.Equal(lines.IndexOf("It has retired 3 damaged areas so far.") + 1, grown);
        Assert.Contains(card.Evidence, e => e.Trend == TrendKind.CounterGrowth);
        Assert.DoesNotContain(AdvicePc.TrendMessage, AllText(card), StringComparison.Ordinal);
    }

    [Fact]
    public void A_cable_error_trend_alone_is_not_a_failing_drive()
    {
        var section = SystemSsd().Drive("smart.crc_errors", 3).Growth("smart.crc_errors", 1, 3).Advice();

        Silent(section);
        Assert.Equal(2, section.OtherFindings);
    }

    [Theory]
    [InlineData("storage.wear_pct", 69, false)]
    [InlineData("storage.wear_pct", 70, true)]
    [InlineData("storage.wear_pct", 95, true)]
    [InlineData("smart.ssd_percent_consumed", 69, false)]
    [InlineData("smart.ssd_percent_consumed", 75, true)]
    public void Endurance_mostly_used_plans_a_replacement(string metricId, double pct, bool fires)
    {
        var section = SystemSsd().Drive(metricId, pct).Advice();
        if (!fires)
        {
            Silent(section);
            return;
        }

        var card = Only(section, "H1");
        Assert.Equal((AdviceUrgency.Plan, 20), (card.Urgency, card.Rank));
        Assert.Equal("Plan to replace this drive: it is worn", card.Title.Text);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"It has used {pct}% of its rated write endurance."), Lines(card));
        Assert.Contains("advice.h1.for-it.replace", Keys(card));
        // Worn is not failing: no "everything may be lost", no "do not run a speed test".
        Assert.DoesNotContain("advice.h1.lost", Keys(card));
        Assert.DoesNotContain("advice.h1.do-not.stress", Keys(card));
    }

    [Fact]
    public void A_worn_drive_s_forecast_is_quoted_from_its_numbers_never_its_message()
    {
        var card = Only(SystemSsd().Drive("storage.wear_pct", 75).Projection("storage.wear_pct", 60, 75, Grade.Normal).Advice(), "H1");

        Assert.Contains($"It was at 60% on {Day(AdvicePc.LastScan)}; it is at 75% now.", Lines(card));
        Assert.Contains(card.Evidence, e => e.Trend == TrendKind.Projection);
        Assert.DoesNotContain(AdvicePc.TrendMessage, AllText(card), StringComparison.Ordinal);
    }

    public static TheoryData<string, MetricValue, string?, int?> NotDamage => new()
    {
        { "smart.crc_errors", 500, Ssd, 0 },
        { "smart.command_timeout", 200, Ssd, 0 },
        { "smart.temperature_c", 85, Ssd, 0 },
        { "smart.ssd_temperature_c", 85, Ssd, 0 },
        { "storage.temperature_c", 85, Ssd, 0 },
        { "smart.load_cycles", 600000, Ssd, 0 },
        { "events.disk_errors_90d", 47, null, null },
        { "storage.volume.healthy", false, "C:", null },
        { "storage.volume_clean", false, "C:", null },
    };

    [Theory]
    [MemberData(nameof(NotDamage))]
    public void Readings_that_say_nothing_about_damaged_storage_never_make_a_drive_card(string metricId, MetricValue value, string? subject, int? disk)
    {
        var section = SystemSsd().Read(metricId, value, subject, disk).Advice();

        Silent(section);
        Assert.Equal(1, section.OtherFindings); // graded, and left to its own row's tips
    }

    [Fact]
    public void The_auditor_s_USB_drive_never_gets_a_card_however_bad_its_readings()
    {
        var section = SystemSsd().Disk(1, "external", "E:", 58.2)
            .Read("storage.read_errors_uncorrected", 3, AdvicePc.Stick, 1)
            .Read("smart.reallocated", 40, AdvicePc.Stick, 1)
            .Advice();

        Silent(section);
        Assert.Equal(2, section.OtherFindings);
    }

    [Fact]
    public void A_failing_data_drive_inside_the_PC_names_its_letters()
    {
        var card = Only(SystemSsd().Disk(1, "internal", "D:").Drive("smart.reallocated", 5, 1, AdvicePc.DataDisk).Advice(), "H1");

        Assert.Equal(("H1:disk1", AdvicePc.DataDisk, (int?)1), (card.Id, card.Subject, card.DiskNumber));
        Assert.Equal("Back up now: a drive in this PC is failing", card.Title.Text);
        Assert.Equal("It holds D:.", Lines(card)[0]);
    }

    [Fact]
    public void A_drive_with_no_role_row_is_counted_as_inside_the_PC_and_says_it_could_not_tell()
    {
        // No volume-check row for disk 3: the map was unreadable, or the check was not selected.
        var card = Only(SystemSsd().Drive("smart.reallocated", 5, 3, AdvicePc.DataDisk).Advice(), "H1");

        Assert.Equal("Back up now: a drive is failing", card.Title.Text);
        Assert.StartsWith("This scan could not tell whether this drive is inside the PC or plugged in over USB.", Lines(card)[0], StringComparison.Ordinal);
        Assert.DoesNotContain("advice.h1.do.estimate", Keys(card));
        Assert.Contains("advice.h1.for-it.replace.unmeasured", Keys(card));
    }

    [Fact]
    public void A_drive_reading_with_no_disk_number_gets_a_card_of_its_own_by_subject()
    {
        var card = Only(new AdvicePc().Drive("smart.reallocated", 5, disk: null).Advice(), "H1");

        Assert.Equal(("H1:unknown-1", Ssd, (int?)null), (card.Id, card.Subject, card.DiskNumber));
        Assert.Equal("advice.h1.role.unknown", Keys(card)[0]);
    }

    [Fact]
    public void Two_identical_drives_only_one_failing_give_one_card_on_the_right_disk()
    {
        var section = new AdvicePc().Disk(0, "system", "C:").Disk(1, "internal", "D:")
            .Drive("smart.reallocated", 0, 0).Drive("smart.reallocated", 5, 1).Advice();

        Assert.Equal(1, Only(section, "H1").DiskNumber);
    }

    /// <summary>
    /// A desktop with two SSDs of one model: C: at 2 retired areas, D: at none. History keeps one
    /// series for both, so the last scan's samples were 2 and 0, and TrendAnalyzer compared C:'s 2
    /// with D:'s 0: "grown from 0 to 2", every scan. With D: another model the series is C:'s own.
    /// </summary>
    [Theory]
    [InlineData(Ssd, AdviceUrgency.CheckAgain)]
    [InlineData(AdvicePc.DataDisk, AdviceUrgency.ActNow)]
    public void A_count_two_drives_of_one_model_share_a_history_of_is_never_said_to_have_grown(string secondDrive, AdviceUrgency expected)
    {
        var section = new AdvicePc().Disk(0, "system", "C:").Disk(1, "internal", "D:")
            .Drive("smart.reallocated", 2, 0).Drive("smart.reallocated", 0, 1, secondDrive)
            .Growth("smart.reallocated", 0, 2).Advice();

        var card = Only(section, "H1"); // and no card for a drive "this scan could not place"
        Assert.Equal(("H1:disk0", expected), (card.Id, card.Urgency));
        if (expected == AdviceUrgency.ActNow)
        {
            Assert.Contains("advice.h1.growth", Keys(card));
            Assert.Equal(0, section.OtherFindings);
            return;
        }
        Assert.Equal("Back up this drive, then scan again in 1-2 weeks", card.Title.Text);
        Assert.DoesNotContain("advice.h1.growth", Keys(card));
        Assert.DoesNotContain(card.Evidence, e => e.Trend is not null);
        Assert.Equal(1, section.OtherFindings); // the trend is left to its own row
        // Scanning again cannot tell either: the card says so, and does not promise the app will.
        Assert.Contains("Write down the numbers above, then scan again in one to two weeks and compare them yourself: another drive " +
                        "this scan read is the same model, and the app's history cannot tell the two drives' readings apart. If a " +
                        "count has grown, replace the drive.", Lines(card));
        Assert.DoesNotContain("advice.h1.check-again.rescan", Keys(card));
    }

    [Fact]
    public void Two_drives_of_one_model_at_the_same_count_get_a_card_each_and_none_for_a_drive_the_scan_could_not_place()
    {
        var section = new AdvicePc().Disk(0, "system", "C:").Disk(1, "internal", "D:")
            .Drive("smart.reallocated", 5, 0).Drive("smart.reallocated", 5, 1)
            .Growth("smart.reallocated", 3, 5).Advice();

        Assert.Empty(section.ItemErrors);
        Assert.Equal(["H1:disk0", "H1:disk1"], section.Recommendations.Select(c => c.Id));
        Assert.DoesNotContain(section.Recommendations.SelectMany(Keys), k => k is "advice.h1.role.unknown" or "advice.h1.growth");
    }

    [Fact]
    public void A_worn_drive_s_forecast_is_left_out_when_another_drive_of_its_model_shares_its_history()
    {
        // The forecast's "It was at 20%" is the series' first sample, here the other drive's.
        var card = Only(new AdvicePc().Disk(0, "system", "C:").Disk(1, "internal", "D:")
            .Drive("storage.wear_pct", 75, 0).Drive("storage.wear_pct", 20, 1)
            .Projection("storage.wear_pct", 20, 75, Grade.Normal).Advice(), "H1");

        Assert.Equal((AdviceUrgency.Plan, 0), (card.Urgency, card.DiskNumber));
        Assert.DoesNotContain("advice.h1.projection", Keys(card));
    }

    [Fact]
    public void Without_the_space_in_use_the_card_never_prints_a_size_it_does_not_have()
    {
        var card = Only(new AdvicePc().Disk(0, "system", "C:").Drive("smart.reallocated", 5).Advice(), "H1");

        Assert.DoesNotContain("advice.h1.do.estimate", Keys(card));
        Assert.Contains("Replace it with a known-brand SSD at least 1.5 times the size of the space in use, with the same connection " +
                        "and physical size as the old one: check the PC's manual.", Lines(card));
        Assert.DoesNotMatch(@"\d\s*GB", AllText(card));
    }

    [Fact]
    public void Without_a_known_system_drive_the_card_does_not_name_a_letter()
    {
        var card = Only(SystemSsd().Drive("smart.reallocated", 5).Advice(systemDrive: null), "H1");

        Assert.Equal("This is the drive Windows runs from.", Lines(card)[0]);
    }

    public static TheoryData<bool?, bool?, bool?, string[]> Protection => new()
    {
        { true, false, true, ["advice.h1.protection.onedrive"] },
        { false, true, true, ["advice.h1.protection.file-history"] },
        // File History backs up folders OneDrive does not: "anything else is not backed up" would be false.
        { true, true, true, ["advice.h1.protection.onedrive.folders", "advice.h1.protection.file-history"] },
        { true, null, true, ["advice.h1.protection.onedrive.folders"] },  // File History could not be read: not claimed off

        { false, false, false, ["advice.h1.protection.none"] },
        { false, false, true, ["advice.h1.protection.shadow-only"] },    // shadow copies only
        { false, false, null, ["advice.h1.protection.unknown"] },        // shadow copies not counted: no conclusion
        { false, null, null, ["advice.h1.protection.unknown"] },         // File History could not be read
        { null, null, null, ["advice.h1.protection.unknown"] },          // the backup check did not run
    };

    [Theory]
    [MemberData(nameof(Protection))]
    public void The_card_says_what_already_protects_this_account_s_files(bool? oneDrive, bool? fileHistory, bool? any, string[] expected)
    {
        var pc = SystemSsd().Drive("smart.reallocated", 5);
        if (oneDrive is { } od) pc.Info("backup.onedrive_folders", od);
        if (fileHistory is { } fh) pc.Read("backup.file_history", fh);
        if (any is { } a) pc.Read("backup.any_protection", a);

        var card = Only(pc.Advice(), "H1");

        Assert.Equal(expected, Keys(card).Where(k => k.StartsWith("advice.h1.protection.", StringComparison.Ordinal)));
    }

    [Fact]
    public void Shadow_copies_are_never_said_to_be_on_the_failing_drive_because_the_scan_does_not_know_where_they_are()
    {
        // System Restore keeps its shadow copies of C: on C:; the failing drive here is D:.
        var card = Only(SystemSsd().Disk(1, "internal", "D:").Drive("smart.reallocated", 5, 1, AdvicePc.DataDisk)
            .Info("backup.onedrive_folders", false).Read("backup.file_history", false).Read("backup.any_protection", true)
            .Advice(), "H1");

        Assert.Contains("Only Windows' shadow copies were found, and they are no protection against a drive failing.", Lines(card));
        Assert.DoesNotContain("same drive", AllText(card), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(14.6, "About 15 GB is in use on D:. Copying all of it to a USB 3 drive takes 5 minutes or less at a typical 50 to 100 MB/s.")]
    [InlineData(14.7, "About 15 GB is in use on D:. Copying all of it to a USB 3 drive takes about 5 to 10 minutes at a typical 50 to 100 MB/s.")]
    public void A_copy_time_whose_two_ends_round_to_the_same_minutes_is_not_written_as_a_range(double usedGb, string line)
    {
        var card = Only(SystemSsd().Disk(1, "internal", "D:", usedGb).Drive("smart.reallocated", 5, 1, AdvicePc.DataDisk).Advice(), "H1");

        Assert.Contains(line, Lines(card));
        Assert.DoesNotContain("5 to 5", AllText(card), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "system", "About 293 GB is in use on C:, Windows and programs included. Copying all of it to a USB 3 drive takes " +
                             "about 50 to 100 minutes at a typical 50 to 100 MB/s; your own files are usually much less.")]
    [InlineData(1, "internal", "About 293 GB is in use on D:. Copying all of it to a USB 3 drive takes about 50 to 100 minutes at a " +
                               "typical 50 to 100 MB/s.")]
    public void Only_the_drive_Windows_runs_from_is_said_to_hold_Windows_and_programs(int disk, string role, string line)
    {
        var letters = disk == 0 ? "C:" : "D:";
        var pc = disk == 0 ? new AdvicePc() : SystemSsd();
        var card = Only(pc.Disk(disk, role, letters, 292.9).Drive("smart.reallocated", 5, disk, AdvicePc.DataDisk).Advice(), "H1");

        Assert.Contains(line, Lines(card));
    }

    [Fact]
    public void A_failing_system_drive_takes_the_space_card_s_place()
    {
        var section = SystemSsd().Drive("smart.reallocated", 5).Read("storage.volume.free_pct", 8, "C:").Advice();

        var card = Only(section, "H1");
        Assert.Single(section.Recommendations);
        Assert.Contains("C: is also low on space (8% free). Freeing space does not fix a failing drive: copy your files off first.", Lines(card));
        Assert.Contains(card.Evidence, e => e.MetricId == AdviceFacts.FreeSpaceId);
        Assert.Equal(0, section.OtherFindings);
    }

    [Fact]
    public void A_failing_drive_that_may_be_the_system_drive_also_takes_the_space_card_s_place()
    {
        var section = new AdvicePc().Drive("smart.reallocated", 5, 3).Read("storage.volume.free_pct", 8, "C:").Advice();

        var card = Only(section, "H1");
        Assert.Single(section.Recommendations);
        Assert.Contains("If this is the drive Windows runs from: C: is also low on space. Free space only after your files are copied.", Lines(card));
    }

    [Fact]
    public void A_failing_data_drive_leaves_the_space_card_and_says_nothing_about_C()
    {
        var section = SystemSsd().Disk(1, "internal", "D:").Drive("smart.reallocated", 5, 1, AdvicePc.DataDisk)
            .Read("storage.volume.free_pct", 8, "C:").Advice();

        Assert.Equal(["H1", "SPACE"], section.Recommendations.Select(c => c.Item));
        Assert.DoesNotContain(Keys(section.Recommendations[0]), k => k.StartsWith("advice.h1.also-low.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_worn_system_drive_is_not_failing_and_leaves_the_space_card()
    {
        var section = SystemSsd().Drive("storage.wear_pct", 80).Read("storage.volume.free_pct", 8, "C:").Advice();

        Assert.Equal(["H1", "SPACE"], section.Recommendations.Select(c => c.Item));
        Assert.DoesNotContain(Keys(section.Recommendations[0]), k => k.StartsWith("advice.h1.also-low.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_counter_that_grows_inside_one_grade_brings_a_hidden_card_back()
    {
        // With history off, 5 retired areas becoming 9 keeps the grade Bad and makes no trend.
        string Print(double count) => Only(SystemSsd().Drive("smart.reallocated", count).Advice(), "H1").Fingerprint;

        Assert.Equal(Print(5), Print(5));
        Assert.NotEqual(Print(5), Print(9));
    }

    // --- O4: security settings switched off ---------------------------------------------------------

    private static AdvicePc Firewalls(bool domain = true, bool @private = true, bool @public = true) => new AdvicePc()
        .Read("security.firewall.enabled", domain, "Domain profile")
        .Read("security.firewall.enabled", @private, "Private profile")
        .Read("security.firewall.enabled", @public, "Public profile");

    [Fact]
    public void Every_security_setting_on_is_silent() =>
        Silent(Firewalls().Read("security.av.registered", true).Read("security.defender.rtp", true).Read("security.defender.tamper", true)
            .Read("security.uac.enabled", true).Read("security.smartscreen", "Warn").Advice());

    [Fact]
    public void The_public_firewall_off_gives_one_card_with_one_setting()
    {
        var card = Only(Firewalls(@public: false).Advice(), "O4");

        Assert.Equal(("O4", "security", 40, AdviceUrgency.Advice, Grade.Bad), (card.Id, card.Area, card.Rank, card.Urgency, card.Severity));
        Assert.Equal("Security settings are switched off", card.Title.Text);
        Assert.Equal(
        [
            "Windows Firewall is off for the Public profile.",
            "Open Windows Security from the Start menu and turn them back on: Firewall & network protection, and Virus & threat " +
            "protection, then Manage settings.",
            "On a company laptop your IT team may have set this; ask them first. If another security product is in charge, check " +
            "that product's settings instead.",
        ], Lines(card));
    }

    [Fact]
    public void Three_settings_off_give_one_card_with_three_settings()
    {
        var section = Firewalls(@public: false).Read("security.uac.enabled", false).Read("security.smartscreen", "Off").Advice();

        var card = Only(section, "O4");
        Assert.Equal(["Windows Firewall is off for the Public profile.", "User Account Control is off.", "SmartScreen is off."],
            card.Lines.Where(l => l.Key.StartsWith("advice.o4.setting.", StringComparison.Ordinal)).Select(l => l.Text));
        Assert.Contains("advice.o4.do.uac", Keys(card));
        Assert.Contains("advice.o4.do.smartscreen", Keys(card));
        Assert.DoesNotContain("advice.o4.why", Keys(card));
        Assert.Equal(3, card.Evidence.Count);
    }

    [Fact]
    public void Real_time_protection_off_while_another_antivirus_is_in_charge_is_silent()
    {
        Silent(new AdvicePc().Info("security.defender.rtp", false).Advice());

        var card = Only(new AdvicePc().Read("security.defender.rtp", false).Advice(), "O4");
        Assert.Contains("Microsoft Defender real-time protection is off.", Lines(card));
        Assert.Contains("advice.o4.why", Keys(card));
    }

    [Theory]
    [InlineData("security.av.registered", false, "No antivirus is active.", true)]
    [InlineData("security.defender.tamper", false, "Defender tamper protection is off.", false)]
    public void Each_setting_has_its_sentence(string metricId, bool value, string line, bool warnsOfMalware)
    {
        var card = Only(new AdvicePc().Read(metricId, value).Advice(), "O4");

        Assert.Contains(line, Lines(card));
        Assert.Equal(warnsOfMalware, Keys(card).Contains("advice.o4.why"));
    }

    [Theory]
    [InlineData("Warn", false)]
    [InlineData("RequireAdmin", false)]
    [InlineData("SomethingNew", false)] // graded Normal: a state this app does not know is not "off"
    [InlineData("Off", true)]
    public void SmartScreen_counts_only_when_it_is_off(string state, bool fires)
    {
        var section = new AdvicePc().Read("security.smartscreen", state).Advice();

        if (fires) Assert.Equal(["SmartScreen is off.", "In Windows Security, open App & browser control."], Lines(Only(section, "O4")).Take(2));
        else Silent(section);
    }

    [Fact]
    public void A_firewall_reading_with_no_profile_still_says_the_firewall_is_off() =>
        Assert.Equal("Windows Firewall is off for one of its network profiles.",
            Lines(Only(new AdvicePc().Read("security.firewall.enabled", false).Advice(), "O4"))[0]);

    // --- O1: Windows 11 blocked only by a firmware switch ---------------------------------------------

    [Theory]
    [InlineData("Tpm", true)]
    [InlineData("TpmVersion", true)]
    [InlineData("SecureBoot", true)]
    [InlineData("Tpm UefiSecureBoot", true)]
    [InlineData("tpm", true)]               // Windows' codes, compared as the kit compares them
    [InlineData("CpuFms", false)]           // all three pilots and the T470 fleet
    [InlineData("CpuFms Tpm", false)]       // a processor block cannot be switched off
    [InlineData("PopCnt", false)]
    [InlineData("SystemDriveSize", false)]
    [InlineData("Memory", false)]
    [InlineData("Tpm SomethingNew", false)] // an unknown code is not guessed at
    [InlineData("none", false)]
    [InlineData("", false)]
    public void Windows_11_advice_needs_every_blocker_to_be_a_firmware_switch(string blockers, bool fires)
    {
        var section = new AdvicePc().Read("os.win11_ready", false).Info("os.win11_blockers", blockers).Advice();
        if (!fires)
        {
            Silent(section);
            return;
        }

        var card = Only(section, "O1");
        Assert.Equal(("O1", "windows", 50, AdviceUrgency.Advice), (card.Id, card.Area, card.Rank, card.Urgency));
    }

    [Fact]
    public void A_TPM_switched_off_reads_in_full()
    {
        var card = Only(new AdvicePc().Read("os.win11_ready", false).Info("os.win11_blockers", "Tpm").Advice(), "O1");

        Assert.Equal("For IT: Windows 11 is blocked only by a firmware setting", card.Title.Text);
        Assert.Equal(
        [
            (AdviceLineRole.Finding, "The TPM security chip is off or not ready: turn on Intel PTT or AMD fTPM (the TPM) in the BIOS/UEFI settings."),
            (AdviceLineRole.Finding, "Probably ready for Windows 11 after the change: nothing else blocks it."),
            (AdviceLineRole.ForIt, "Before any TPM or BIOS change: if BitLocker is on, suspend it and keep the recovery key."),
            (AdviceLineRole.Do, "Windows checks again by itself about once a day: leave the PC on for a day, then scan again or run the kit again."),
        ], card.Lines.Select(l => (l.Role, l.Text)));
        Assert.Equal("os.win11_ready", Assert.Single(card.Evidence).MetricId);
    }

    [Fact]
    public void A_legacy_BIOS_blocker_warns_that_switching_stops_Windows_starting()
    {
        var card = Only(new AdvicePc().Read("os.win11_ready", false).Info("os.win11_blockers", "Tpm Uefi").Advice(), "O1");

        Assert.Contains("advice.o1.blocker.uefi", Keys(card));
        Assert.Contains("advice.o1.for-it.mbr2gpt", Keys(card));
    }

    [Fact]
    public void On_Windows_11_the_hardware_support_row_decides_and_the_card_says_Windows_11_runs()
    {
        var card = Only(new AdvicePc().Read("os.win11_hardware_supported", false).Info("os.win11_blockers", "SecureBoot").Advice(), "O1");

        Assert.Contains("Windows 11 runs here, but Microsoft supports it on this PC only once these are switched on.", Lines(card));
        Assert.Contains("advice.o1.for-it.mbr2gpt", Keys(card));
    }

    [Fact]
    public void A_PC_Windows_says_is_ready_or_with_no_verdict_is_silent()
    {
        Silent(new AdvicePc().Read("os.win11_ready", true).Info("os.win11_blockers", "Tpm").Advice());
        Silent(new AdvicePc().Read("os.win11_ready", false).Advice()); // the verdict predates this install: no blocker row
        Silent(new AdvicePc().Info("os.win11_blockers", "Tpm").Advice());
    }

    [Theory]
    [InlineData("SecureBoot", null, null, true, "advice.o1.now-on.secureboot")]
    [InlineData("SecureBoot", null, null, false, "advice.o1.blocker.secureboot")]
    [InlineData("SecureBoot", null, null, null, "advice.o1.blocker.secureboot")] // not read: Windows' verdict stands
    [InlineData("UefiSecureBoot", null, null, true, "advice.o1.now-on.secureboot")]
    [InlineData("Tpm", true, "2.0", null, "advice.o1.now-on.tpm")]
    [InlineData("TpmVersion", true, "2.0", null, "advice.o1.now-on.tpm")]
    [InlineData("Tpm", true, "1.2", null, "advice.o1.blocker.tpm")]
    [InlineData("Tpm", true, null, null, "advice.o1.blocker.tpm")]     // ready, but not known to be 2.0
    [InlineData("Tpm", false, "2.0", null, "advice.o1.blocker.tpm")]
    [InlineData("Tpm", null, null, true, "advice.o1.blocker.tpm")]     // Secure Boot on says nothing about the TPM
    [InlineData("Uefi", null, null, true, "advice.o1.blocker.uefi")]   // legacy BIOS mode has no reading of its own
    public void A_setting_this_scan_reads_as_on_is_not_sent_back_to_be_switched_on(
        string blockers, bool? tpmReady, string? tpmVersion, bool? secureBoot, string key)
    {
        // Windows checks about once a day: IT switches the setting on, scans again at once, and the
        // verdict still names it while the platform check reads it as on.
        var pc = new AdvicePc().Read("os.win11_ready", false).Info("os.win11_blockers", blockers);
        if (tpmReady is { } ready) pc.Read("security.tpm_ready", ready);
        if (tpmVersion is { } version) pc.Info("security.tpm_version", version);
        if (secureBoot is { } on) pc.Read("security.secure_boot", on);

        var card = Only(pc.Advice(), "O1");

        Assert.Equal(key, Keys(card)[0]);
        Assert.Equal("advice.o1.do.check-again", Keys(card)[^1]);
    }

    [Fact]
    public void A_TPM_switched_on_since_Windows_last_checked_reads_in_full()
    {
        var card = Only(new AdvicePc().Read("os.win11_ready", false).Info("os.win11_blockers", "Tpm TpmVersion")
            .Read("security.tpm_ready", true).Info("security.tpm_version", "2.0").Advice(), "O1");

        Assert.Equal(
        [
            "Windows' last check found no TPM 2.0 security chip turned on, but this scan reads a TPM 2.0 as on: Windows has not checked again since.",
            "Probably ready for Windows 11 after the change: nothing else blocks it.",
            "Before any TPM or BIOS change: if BitLocker is on, suspend it and keep the recovery key.",
            "Windows checks again by itself about once a day: leave the PC on for a day, then scan again or run the kit again.",
        ], Lines(card));
    }

    // --- SPACE: the system drive is low on space ------------------------------------------------------

    [Theory]
    [InlineData(21, null)]
    [InlineData(20, "C: is getting full: 20% free")]
    [InlineData(15, "C: is getting full: 15% free")]
    [InlineData(10, "C: is getting full: 10% free")]
    [InlineData(9.9, "C: is running out of space: 9.9% free. Under 10% free, Windows Update can fail.")]
    [InlineData(8, "C: is running out of space: 8% free. Under 10% free, Windows Update can fail.")]
    public void The_space_card_starts_at_the_free_space_row_s_own_band_edge(double freePct, string? title)
    {
        var section = new AdvicePc().Read("storage.volume.free_pct", freePct, "C:").Advice();
        if (title is null)
        {
            Silent(section);
            return;
        }

        var card = Only(section, "SPACE");
        Assert.Equal(("SPACE:C:", "space", 45, (string?)null), (card.Id, card.Area, card.Rank, card.Subject));
        Assert.Equal(title, card.Title.Text);
        // The hibernation file is mentioned only when the drive is graded Bad.
        Assert.Equal(freePct < 10, Keys(card).Contains("advice.space.d4"));
    }

    [Fact]
    public void The_space_card_goes_safest_first_and_names_no_size()
    {
        var card = Only(new AdvicePc().Read("storage.volume.free_pct", 8, "C:").Advice(), "SPACE");

        Assert.Equal(
        [
            "advice.space.d7", "advice.space.storage-sense", "advice.space.d7.restore-points", "advice.space.d5", "advice.space.d4",
            "advice.space.d6", "advice.space.d8", "advice.space.d9", "advice.space.graded",
        ], Keys(card));
        Assert.StartsWith("Open Disk Cleanup (Win+R, type cleanmgr, choose C:)", Lines(card)[0], StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\d\s*(GB|MB)", AllText(card));
    }

    [Fact]
    public void A_labelled_system_volume_still_counts_and_its_label_is_never_printed()
    {
        var card = Only(new AdvicePc().Read("storage.volume.free_pct", 15, "C: (Personal label)").Advice(), "SPACE");

        Assert.Equal("C: is getting full: 15% free", card.Title.Text);
        Assert.DoesNotContain("Personal label", AllText(card), StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_USB_drive_or_a_full_data_drive_is_silent()
    {
        Silent(new AdvicePc().Info("storage.volume.free_pct", 5, "E:").Advice());
        Silent(new AdvicePc().Read("storage.volume.free_pct", 5, "D:").Read("storage.volume.free_pct", 60, "C:").Advice());
    }

    [Fact]
    public void The_drive_Windows_runs_from_is_named_from_the_context_not_assumed_to_be_C()
    {
        var card = Only(new AdvicePc().Read("storage.volume.free_pct", 60, "C:").Read("storage.volume.free_pct", 8, "D:").Advice("D:"), "SPACE");

        Assert.Equal("SPACE:D:", card.Id);
        Assert.StartsWith("D: is running out of space", card.Title.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("events.bugcheck_90d", 1, "advice.space.d7.dumps")]
    [InlineData("events.gpu_tdr_90d", 3, "advice.space.d7.dumps")]
    [InlineData("integrity.image_health", "Repairable", "advice.space.d7.repair-first")]
    public void Crashes_and_a_repairable_image_add_their_warnings(string metricId, object value, string key)
    {
        var low = new AdvicePc().Read("storage.volume.free_pct", 15, "C:");
        Assert.DoesNotContain(key, Keys(Only(low.Advice(), "SPACE")));

        var card = Only(low.Read(metricId, value is string s ? s : Convert.ToDouble(value, CultureInfo.InvariantCulture)).Advice(), "SPACE");

        Assert.Contains(key, Keys(card));
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("NonRepairable")]
    public void Only_an_image_Windows_calls_repairable_adds_the_repair_first_line(string state) =>
        Assert.DoesNotContain("advice.space.d7.repair-first",
            Keys(Only(new AdvicePc().Read("storage.volume.free_pct", 15, "C:").Read("integrity.image_health", state).Advice(), "SPACE")));

    [Fact]
    public void OneDrive_folder_backup_adds_the_free_up_space_line()
    {
        var pc = new AdvicePc().Read("storage.volume.free_pct", 15, "C:").Info("backup.onedrive_folders", false);
        Assert.DoesNotContain("advice.space.d8.onedrive", Keys(Only(pc.Advice(), "SPACE")));

        Assert.Contains("advice.space.d8.onedrive", Keys(Only(pc.Info("backup.onedrive_folders", true).Advice(), "SPACE")));
    }

    // --- O3: hot and slow -----------------------------------------------------------------------------

    [Theory]
    [InlineData(91, 100, false)] // hot, but still at its base speed
    [InlineData(70, 78, false)]  // slow, but cool
    [InlineData(79, 99, false)]
    [InlineData(80, 99, true)]   // both just past their band edges
    [InlineData(91, 78, true)]
    public void Heat_advice_needs_a_hot_processor_and_a_slow_one(double celsius, double clockPct, bool fires)
    {
        var section = new AdvicePc().Read("cpu.temperature_c", celsius, AdvicePc.Cpu)
            .Read("bench.cpu.clock_pct_of_base", clockPct, AdvicePc.Cpu).Advice();

        if (!fires)
        {
            Silent(section);
            return;
        }
        var card = Only(section, "O3");
        Assert.Equal(("O3", "heat", 25), (card.Id, card.Area, card.Rank));
        Assert.Equal(string.Create(CultureInfo.InvariantCulture,
            $"With one core busy, the processor ran at {clockPct}% of its base speed; a healthy one reaches 100% or more. Earlier in this scan it read {celsius} °C."),
            Lines(card)[0]);
        Assert.Equal(2, card.Evidence.Count);
    }

    [Fact]
    public void Heat_advice_speaks_of_a_charger_and_a_lap_only_where_a_battery_was_read()
    {
        // A desktop has no battery, so the speed test counts it as on mains and grades its speed.
        var desktop = Only(new AdvicePc().Read("cpu.temperature_c", 85, AdvicePc.Cpu)
            .Read("bench.cpu.clock_pct_of_base", 92, AdvicePc.Cpu).Advice(), "O3");
        var laptop = Only(new AdvicePc().Read("cpu.temperature_c", 85, AdvicePc.Cpu)
            .Read("bench.cpu.clock_pct_of_base", 92, AdvicePc.Cpu).Read("battery.capacity_pct", 85, "Battery").Advice(), "O3");

        Assert.Equal(["advice.o3.finding", "advice.o3.why.pc", "advice.o3.do.vents.pc", "advice.o3.for-it.clean", "advice.o3.for-it.bios.pc",
            "advice.o3.next-scan"], Keys(desktop));
        Assert.DoesNotMatch(@"(?i)laptop|charger|\blap\b", AllText(desktop));
        Assert.Equal(["advice.o3.finding", "advice.o3.why", "advice.o3.do.vents", "advice.o3.do.charger", "advice.o3.for-it.clean",
            "advice.o3.for-it.bios", "advice.o3.next-scan"], Keys(laptop));
        Assert.Equal(desktop.Evidence, laptop.Evidence);
    }

    [Fact]
    public void A_speed_measured_on_battery_is_not_graded_and_is_silent() =>
        Silent(new AdvicePc().Read("cpu.temperature_c", 91, AdvicePc.Cpu).Info("bench.cpu.clock_pct_of_base", 60, AdvicePc.Cpu).Advice());

    [Fact]
    public void The_thermal_zone_near_the_processor_counts_when_it_is_all_Windows_gives() =>
        Assert.Contains("85 °C", Lines(Only(new AdvicePc().Read("cpu.zone_temperature_c", 85, "ACPI thermal zone")
            .Read("bench.cpu.clock_pct_of_base", 80, AdvicePc.Cpu).Advice(), "O3"))[0], StringComparison.Ordinal);

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, null)]
    [InlineData(1.0, "Windows also logged 1 time in the last 30 days that the firmware held the processor back.")]
    [InlineData(14.0, "Windows also logged 14 times in the last 30 days that the firmware held the processor back.")]
    public void The_firmware_s_speed_limits_are_quoted_and_never_trigger(double? times, string? line)
    {
        var pc = new AdvicePc().Read("cpu.temperature_c", 91, AdvicePc.Cpu).Read("bench.cpu.clock_pct_of_base", 78, AdvicePc.Cpu);
        if (times is { } t) pc.Info("events.cpu_speed_limited_30d", t);

        var lines = Lines(Only(pc.Advice(), "O3"));

        if (line is null) Assert.DoesNotContain(lines, l => l.StartsWith("Windows also logged", StringComparison.Ordinal));
        else Assert.Equal(line, lines[1]);
        Silent(new AdvicePc().Info("events.cpu_speed_limited_30d", 500).Advice());
    }

    // --- S1 and S4: restart once ------------------------------------------------------------------------

    [Theory]
    [InlineData(7.0, true, 50, false)]
    [InlineData(7.1, true, 50, true)]
    [InlineData(25, false, 79, false)]
    [InlineData(25, false, 80, true)]
    [InlineData(25, false, 86, true)]
    [InlineData(45, false, 50, false)] // a long uptime alone is the uptime row's own tip
    [InlineData(3, true, 95, false)]
    public void Restart_advice_needs_more_than_a_week_up_and_a_restart_waiting_or_memory_promised(
        double days, bool pending, double commitPct, bool fires)
    {
        var section = new AdvicePc().Read("os.uptime_days", days).Read("os.pending_reboot", pending).Read("memory.commit_pct", commitPct).Advice();

        if (!fires)
        {
            Silent(section);
            return;
        }
        var card = Only(section, "S1");
        Assert.Equal(("S1", "startup", 60), (card.Id, card.Area, card.Rank));
        Assert.Equal("Restart Windows once (Restart, not Shut down)", card.Title.Text);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"Windows has not fully restarted for {Math.Round(days)} days."), Lines(card)[0]);
        Assert.Equal(pending, Keys(card).Contains("advice.s1.pending"));
        Assert.Equal(commitPct >= 80, Keys(card).Contains("advice.s1.commit"));
    }

    private static AdvicePc NeedsARestart(AdvicePc pc) => pc.Read("os.uptime_days", 9).Read("os.pending_reboot", true);

    [Theory]
    [InlineData("smart.reallocated", 5, "system", "advice.s4.failing-drive")]
    [InlineData("smart.reallocated", 3, "system", "advice.s4.failing-drive")]    // check again is failing too
    [InlineData("smart.reallocated", 5, null, "advice.s4.failing-drive.unknown")] // no role row
    [InlineData("smart.reallocated", 5, "internal", null)]                       // a data drive: a restart still helps
    [InlineData("storage.wear_pct", 80, "system", null)]                         // worn is not failing
    [InlineData("smart.reallocated", 0, "system", null)]                         // a healthy SSD
    public void A_restart_card_says_it_will_not_fix_a_slow_start_when_the_Windows_drive_is_failing(
        string metricId, double value, string? role, string? label)
    {
        var pc = NeedsARestart(new AdvicePc()).Drive(metricId, value);
        if (role is not null) pc.Disk(0, role, "C:", 104.4);

        var section = pc.Advice();

        var restart = Only(section, "S1");
        Assert.Same(restart, section.Recommendations[^1]);
        if (label is null) Assert.DoesNotContain(Keys(restart), k => k.StartsWith("advice.s4.", StringComparison.Ordinal));
        else Assert.Equal(label, Keys(restart)[0]);
    }

    [Fact]
    public void The_failing_drive_label_reads_as_the_plan_wrote_it() =>
        Assert.Equal("This will not fix a slow start: the drive Windows runs from is failing. Back it up first (see above).",
            Lines(Only(NeedsARestart(SystemSsd().Drive("smart.reallocated", 5)).Advice(), "S1"))[0]);

    // --- Not measured, and notes: both directions ---------------------------------------------------

    [Theory]
    [InlineData(ModuleStatus.Failed)]
    [InlineData(ModuleStatus.PartiallyCompleted)]
    public void A_drive_check_that_broke_says_drive_health_was_not_fully_checked(ModuleStatus status)
    {
        var section = new AdvicePc().Module("disk.smart", status).Advice();

        var line = Assert.Single(section.NotMeasured);
        Assert.Equal(("drive-health", "advice.not-measured.drive-health"), (line.Area, line.Text.Key));
        Assert.Equal(["disk.smart"], line.ModuleIds);
        Assert.Equal("Drive health was not fully checked: Drive SMART health did not finish, so a failing drive may not show here.", line.Text.Text);
        Assert.Empty(section.Notes);
        Silent(section);
    }

    [Fact]
    public void Both_drive_checks_broken_are_named_in_one_line() =>
        Assert.Equal("Drive health was not fully checked: Drive SMART health and Drive wear & error counters did not finish, so a " +
                     "failing drive may not show here.",
            Assert.Single(new AdvicePc().Module("disk.smart", ModuleStatus.Failed).Module("disk.reliability", ModuleStatus.PartiallyCompleted)
                .Advice().NotMeasured).Text.Text);

    /// <summary>An NVMe laptop: the SMART check skips itself, the reliability check finished, and disk 0 holds C:.</summary>
    private static AdvicePc NvmeLaptop() => SystemSsd().Module("disk.smart", ModuleStatus.Skipped);

    [Fact]
    public void An_NVMe_laptop_whose_error_counters_were_read_gets_no_line_and_no_note()
    {
        var section = NvmeLaptop().Drive("storage.read_errors_uncorrected", 0).Drive("storage.write_errors_uncorrected", 0)
            .Drive("storage.wear_pct", 3).Advice();

        Silent(section);
        Assert.Empty(section.NotMeasured);
        Assert.Empty(section.Notes);
    }

    private const string NotReported = "A drive in this PC did not report whether it has damaged areas or errors, so a failing drive may not show here.";

    [Theory]
    [InlineData("wear only")]    // pilot zip #1's NVMe: wear and a temperature, no error counter
    [InlineData("empty record")] // Windows returned a placeholder: the volume check's row is all there is
    [InlineData("no drive")]     // and without the volume check, no drive inside the PC is known at all
    public void A_drive_inside_the_PC_that_reported_no_damage_counters_gets_a_grey_note(string kind)
    {
        var pc = kind switch
        {
            "wear only" => NvmeLaptop().Drive("storage.wear_pct", 3).Drive("storage.temperature_c", 38),
            "empty record" => NvmeLaptop(),
            _ => new AdvicePc().Module("disk.smart", ModuleStatus.Skipped),
        };

        var section = pc.Advice(ranElevated: true);

        Silent(section);
        Assert.Empty(section.NotMeasured);
        Assert.Equal(NotReported, Assert.Single(section.Notes).Text);
    }

    [Fact]
    public void Every_drive_inside_the_PC_must_report_its_counters_but_a_USB_drive_need_not()
    {
        // Disk 1 is an internal data drive whose record was empty; disk 2 is the auditor's USB stick.
        var read = NvmeLaptop().Drive("storage.read_errors_uncorrected", 0);
        Assert.Empty(read.Disk(2, "external", "E:", 58.2).Advice().Notes);

        Assert.Equal(NotReported, Assert.Single(read.Disk(1, "internal", "D:").Advice().Notes).Text);
    }

    [Theory]
    [InlineData("smart.reallocated", 5, false)] // back up now: nothing a broken check adds about this drive
    [InlineData("smart.reallocated", 3, true)]  // check again: the counters not read may say worse
    [InlineData("storage.wear_pct", 80, true)]  // worn: it may be failing as well
    public void A_broken_drive_check_goes_unsaid_only_when_every_drive_inside_already_says_back_up_now(string metricId, double value, bool line)
    {
        var section = SystemSsd().Drive(metricId, value).Module("disk.reliability", ModuleStatus.Failed).Advice();

        Assert.Single(section.Recommendations);
        Assert.Equal(line, section.NotMeasured.Count == 1);
    }

    [Fact]
    public void A_card_on_one_drive_does_not_hide_that_the_check_for_another_broke()
    {
        // Disk 0, the NVMe drive Windows runs from, went unread: the reliability check timed out. Disk 1,
        // a SATA SSD, answered the SMART check and is worn. Its Plan card says nothing about disk 0.
        var section = SystemSsd().Disk(1, "internal", "D:").Drive("smart.ssd_percent_consumed", 75, 1, AdvicePc.DataDisk)
            .Module("disk.reliability", ModuleStatus.Failed).Advice();

        Assert.Equal(AdviceUrgency.Plan, Only(section, "H1").Urgency);
        var line = Assert.Single(section.NotMeasured);
        Assert.Equal("Drive health was not fully checked: Drive wear & error counters did not finish, so a failing drive may not show here.",
            line.Text.Text);

        // Back up now on both drives: the line would add nothing.
        Assert.Empty(SystemSsd().Drive("smart.reallocated", 5).Disk(1, "internal", "D:").Drive("smart.pending_sectors", 1, 1, AdvicePc.DataDisk)
            .Module("disk.reliability", ModuleStatus.Failed).Advice().NotMeasured);
    }

    [Theory]
    [InlineData(null, null, true)]                     // a standard user's scan: neither ran
    [InlineData(ModuleStatus.Skipped, null, true)]
    [InlineData(ModuleStatus.Skipped, ModuleStatus.Skipped, true)]
    [InlineData(ModuleStatus.Skipped, ModuleStatus.Completed, false)]
    [InlineData(ModuleStatus.Failed, null, false)]     // that one is "not measured" instead
    public void Drive_health_not_being_part_of_the_scan_is_a_grey_note(ModuleStatus? smart, ModuleStatus? reliability, bool note)
    {
        var section = new AdvicePc().Module("disk.smart", smart).Module("disk.reliability", reliability).Advice();

        Assert.Equal(note, section.Notes.Any(n => n.Key == "advice.note.drive-health-not-included"));
        Assert.Equal(smart == ModuleStatus.Failed, section.NotMeasured.Count == 1);
    }

    [Theory]
    [InlineData(false, "Drive health was not part of this scan: its checks need administrator rights.")]
    [InlineData(true, "Drive health was not part of this scan: its checks were not selected.")]
    public void Drive_health_left_out_blames_administrator_rights_only_on_a_scan_that_had_none(bool ranElevated, string note)
    {
        // Elevated: a window started with Run as administrator and both drive checks unticked, or the
        // kit's headless scan from an elevated console without --include-admin.
        var section = SystemSsd().Module("disk.smart", null).Module("disk.reliability", null).Advice(ranElevated: ranElevated);

        Assert.Equal(note, Assert.Single(section.Notes).Text);
        Assert.Empty(section.NotMeasured);
    }

    [Fact]
    public void Free_space_is_not_measured_only_when_the_volume_check_broke_and_C_has_no_reading()
    {
        // TRIM could not be read, so the check is Partial, but C: was measured.
        Assert.Empty(new AdvicePc().Module("volumes", ModuleStatus.PartiallyCompleted)
            .Read("storage.volume.free_pct", 60, "C:").Advice().NotMeasured);

        var line = Assert.Single(new AdvicePc().Module("volumes", ModuleStatus.Failed).Advice().NotMeasured);
        Assert.Equal(("space", "Free space on C: was not measured: Volumes & free space did not finish."), (line.Area, line.Text.Text));
        Assert.Equal("Free space on the drive Windows runs from was not measured: Volumes & free space did not finish.",
            Assert.Single(new AdvicePc().Module("volumes", ModuleStatus.Failed).Advice(systemDrive: null).NotMeasured).Text.Text);
        // Only the USB drive was read: the system drive's free space is still unknown.
        Assert.Single(new AdvicePc().Module("volumes", ModuleStatus.PartiallyCompleted)
            .Info("storage.volume.free_pct", 5, "E:").Advice().NotMeasured);
    }

    [Fact]
    public void Heat_is_not_measured_only_when_the_sensors_broke_and_read_no_processor_temperature()
    {
        var line = Assert.Single(new AdvicePc().Module("sensors", ModuleStatus.Failed).Advice().NotMeasured);
        Assert.Equal(("heat", "advice.not-measured.heat"), (line.Area, line.Text.Key));
        Assert.Equal(["sensors"], line.ModuleIds);

        Assert.Empty(new AdvicePc().Module("sensors", ModuleStatus.PartiallyCompleted)
            .Read("cpu.temperature_c", 60, AdvicePc.Cpu).Advice().NotMeasured);
        Assert.Empty(new AdvicePc().Module("sensors", null).Advice().NotMeasured); // not part of the scan: nothing
    }

    [Fact]
    public void A_hot_processor_whose_speed_test_broke_says_its_slowing_down_was_not_measured()
    {
        var hot = DriveRead().Read("cpu.temperature_c", 91, AdvicePc.Cpu).Module("bench.cpu", ModuleStatus.Failed).Advice();
        Assert.Equal("advice.not-measured.heat-clock", Assert.Single(hot.NotMeasured).Text.Key);
        Assert.Empty(hot.Notes);

        Assert.Empty(new AdvicePc().Read("cpu.temperature_c", 60, AdvicePc.Cpu).Module("bench.cpu", ModuleStatus.Failed).Advice().NotMeasured);
    }

    [Theory]
    [InlineData(91, null, true)]                  // the kit never runs the speed test
    [InlineData(91, ModuleStatus.Skipped, true)]
    [InlineData(91, ModuleStatus.Completed, false)]
    [InlineData(60, null, false)]                 // a cool processor needs no word about its speed
    public void A_hot_processor_with_no_speed_test_gets_a_grey_note_and_no_card(double celsius, ModuleStatus? bench, bool note)
    {
        var pc = DriveRead().Read("cpu.temperature_c", celsius, AdvicePc.Cpu).Module("bench.cpu", bench);
        if (bench == ModuleStatus.Completed) pc.Read("bench.cpu.clock_pct_of_base", 104, AdvicePc.Cpu);

        var section = pc.Advice();

        Silent(section);
        Assert.Empty(section.NotMeasured);
        string[] expected = note ? ["advice.note.clock-not-included"] : [];
        Assert.Equal(expected, section.Notes.Select(n => n.Key));
    }

    [Fact]
    public void Not_measured_lines_and_notes_are_never_cards()
    {
        var section = new AdvicePc().Module("disk.smart", ModuleStatus.Failed).Module("volumes", ModuleStatus.Failed)
            .Module("sensors", ModuleStatus.Failed).Advice();

        Assert.Equal(3, section.NotMeasured.Count);
        Assert.Empty(section.Recommendations);
        Assert.Equal(0, section.OtherFindings);
    }

    // --- Order ---------------------------------------------------------------------------------------

    /// <summary>A PC with one card of every kind: a failing data drive, a worn system SSD, and all the rest.</summary>
    private static AdvicePc EveryKindOfCard() => new AdvicePc()
        .Disk(0, "system", "C:", 104.4).Drive("storage.wear_pct", 80)
        .Disk(1, "internal", "D:").Drive("smart.pending_sectors", 1, 1, AdvicePc.DataDisk)
        .Disk(2, "internal", "E:").Drive("smart.reallocated", 9, 2, "MODEL-D — 500 GB")
        .Read("storage.volume.free_pct", 8, "C:")
        .Read("cpu.temperature_c", 91, AdvicePc.Cpu).Read("bench.cpu.clock_pct_of_base", 78, AdvicePc.Cpu)
        .Read("security.firewall.enabled", false, "Public profile")
        .Read("os.win11_ready", false).Info("os.win11_blockers", "Tpm")
        .Read("os.uptime_days", 9).Read("os.pending_reboot", true);

    [Fact]
    public void Hardware_and_data_come_first_and_Bad_comes_before_Normal_within_a_rank()
    {
        var section = EveryKindOfCard().Advice();

        Assert.Empty(section.ItemErrors);
        // Both drives act now: disk 2 is graded Bad, disk 1 Normal (one pending area), so disk 2 comes first.
        Assert.Equal(["H1:disk2", "H1:disk1", "H1:disk0", "O3", "O4", "SPACE:C:", "O1", "S1"], section.Recommendations.Select(c => c.Id));
        Assert.Equal([Grade.Bad, Grade.Normal], section.Recommendations.Take(2).Select(c => c.Severity));
        Assert.Equal(0, section.OtherFindings);
    }

    [Fact]
    public void The_order_never_depends_on_the_order_the_readings_came_in()
    {
        var pc = EveryKindOfCard();
        var expected = pc.Advice().Recommendations.Select(c => c.Id).ToList();
        var random = new Random(20261028);

        for (var round = 0; round < 100; round++)
        {
            var section = AdviceEngine.ForOs("windows").Derive(
                [.. pc.Verdicts.OrderBy(_ => random.Next())], pc.Trends, pc.Modules, new AdviceContext("C:"))!;
            Assert.Equal(expected, section.Recommendations.Select(c => c.Id));
        }
    }

    // --- What a card may say --------------------------------------------------------------------------

    [Fact]
    public void No_card_quotes_a_reading_s_detail_a_volume_label_a_model_or_a_trend_message()
    {
        const string Secret = @"Found under C:\Users\Example\Secret plans";
        var pc = EveryKindOfCard()
            .Read("security.defender.exclusions_count", 3, detail: "3 exclusion(s) configured. Paths: " + Secret)
            .Without("storage.volume.free_pct").Read("storage.volume.free_pct", 8, "C: (Personal label)")
            .Growth("smart.pending_sectors", 0, 1, AdvicePc.DataDisk)
            .DetailOnEveryReading(Secret);

        var section = pc.Advice();

        Assert.Equal(8, section.Recommendations.Count);
        foreach (var card in section.Recommendations)
        {
            foreach (var forbidden in new[] { "Secret", "Example", "Personal label", "MODEL-", AdvicePc.Cpu, AdvicePc.TrendMessage })
                Assert.DoesNotContain(forbidden, AllText(card), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Secret", JsonSerializer.Serialize(section), StringComparison.Ordinal);
    }

    [GeneratedRegex("\"([a-z][a-z0-9]*(?:\\.[a-z0-9_]+)+)\"")]
    private static partial Regex DottedLiteral();

    [Fact]
    public void Every_reading_the_advice_names_is_one_a_Windows_check_writes()
    {
        // A typo in an id ("smart.realocated") would make an item silent forever, and no other test
        // would notice: the fixtures are written with the same typo.
        var root = AdviceCatalogTests.RepoRoot();
        var named = Directory.GetFiles(Path.Combine(root, "src", "PcHealth.Core", "Advice"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => DottedLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(id => !id.StartsWith("advice.", StringComparison.Ordinal) && !id.StartsWith("list.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var windowsSources = Path.Combine(root, "src", "PcHealth.Modules.Windows");
        var windows = string.Join("\n", Directory.GetFiles(windowsSources, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(windowsSources, f).StartsWith("obj", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));

        foreach (var expected in new[] { "smart.reallocated", "security.smartscreen", "os.win11_blockers", "bench.cpu.clock_pct_of_base", "disk.smart" })
            Assert.Contains(expected, named);
        foreach (var id in named)
            Assert.True(windows.Contains("\"" + id + "\"", StringComparison.Ordinal), $"The advice reads {id}, which no Windows check writes");
    }

    [Fact]
    public void The_numbers_written_into_sentences_are_the_ones_the_code_and_the_rules_use()
    {
        static string Template(string key) => AdviceCatalog.English.Templates(key)[0];
        static Band Band(string metricId, string grade) => RulesLoader.LoadEmbedded("windows").Single(r => r.MetricId == metricId)
            .Bands.First(b => string.Equals(b.Grade, grade, StringComparison.Ordinal));

        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"{AdviceThresholds.ReplacementFactor} times"), Template("advice.h1.for-it.replace"));
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"{AdviceThresholds.ReplacementFactor} times"), Template("advice.h1.for-it.replace.unmeasured"));
        Assert.Contains(string.Create(CultureInfo.InvariantCulture,
            $"{AdviceThresholds.SlowCopyMbPerSecond} to {AdviceThresholds.FastCopyMbPerSecond} MB/s"), Template("advice.h1.do.estimate"));
        Assert.Contains(string.Create(CultureInfo.InvariantCulture,
            $"{AdviceThresholds.SlowCopyMbPerSecond} to {AdviceThresholds.FastCopyMbPerSecond} MB/s"), Template("advice.h1.do.estimate.short"));
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"above {Band("storage.volume.free_pct", "Good").Gt}%"), Template("advice.space.graded"));
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"Under {Band("storage.volume.free_pct", "Normal").Gte}%"), Template("advice.space.title.bad"));
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"reaches {Band("bench.cpu.clock_pct_of_base", "Good").Gte}% or more"), Template("advice.o3.finding"));
    }

    // --- Every sentence, reached by some fixture ------------------------------------------------------

    /// <summary>
    /// Fixtures that between them print every item sentence in the catalog: the failing system drive
    /// of a laptop with everything else wrong too, a data drive checked again, a worn drive and a
    /// failing one, two drives of one model, a drive of unknown role, no system drive known, a hot
    /// desktop whose firmware settings were switched on since Windows last checked, and the checks
    /// that broke, did not run or read no drive.
    /// </summary>
    private static IEnumerable<(string Name, Func<AdviceSection> Section)> Tour()
    {
        yield return ("failing system drive, all else wrong", () => SystemSsd()
            .Disk(0, "system", "C:", 104.4)
            .Read("smart.predict_failure", false, Ssd, 0).Drive("smart.attributes_at_threshold", 1).Drive("smart.reallocated", 12)
            .Drive("smart.pending_sectors", 1).Drive("smart.offline_uncorrectable", 2).Drive("smart.reported_uncorrectable", 7)
            .Drive("storage.read_errors_uncorrected", 1).Drive("storage.write_errors_uncorrected", 40)
            .Drive("storage.wear_pct", 75).Drive("smart.ssd_percent_consumed", 80)
            .Growth("smart.reallocated", 9, 12).Projection("storage.wear_pct", 60, 75, Grade.Normal)
            .Read("storage.volume.free_pct", 8, "C:")
            .Info("backup.onedrive_folders", true).Read("backup.file_history", true)
            .Read("security.firewall.enabled", false, "Public profile").Read("security.firewall.enabled", false)
            .Read("security.av.registered", false).Read("security.defender.rtp", false).Read("security.defender.tamper", false)
            .Read("security.uac.enabled", false).Read("security.smartscreen", "Off")
            .Read("os.win11_hardware_supported", false).Info("os.win11_blockers", "Tpm TpmVersion UefiSecureBoot SecureBoot Uefi")
            .Read("cpu.temperature_c", 91, AdvicePc.Cpu).Read("bench.cpu.clock_pct_of_base", 78, AdvicePc.Cpu)
            .Info("events.cpu_speed_limited_30d", 14).Read("battery.capacity_pct", 85, "Battery")
            .Read("os.uptime_days", 9).Read("os.pending_reboot", true).Read("memory.commit_pct", 86)
            .Advice());
        yield return ("data drive checked again, C: out of space", () => SystemSsd()
            .Disk(1, "internal", "D:", 292.9).Drive("smart.reallocated", 2, 1, AdvicePc.DataDisk)
            .Info("backup.onedrive_folders", false).Read("backup.file_history", false).Read("backup.any_protection", false)
            .Read("storage.volume.free_pct", 8, "C:").Read("events.bugcheck_90d", 1).Read("integrity.image_health", "Repairable")
            .Advice());
        yield return ("worn data drive and a failing one, C: getting full", () => SystemSsd()
            .Disk(1, "internal", "D:").Drive("smart.ssd_percent_consumed", 75, 1, AdvicePc.DataDisk)
            .Disk(2, "internal", "E:", 10).Drive("storage.read_errors_uncorrected", 1, 2, "MODEL-D — 500 GB")
            .Info("backup.onedrive_folders", true).Read("backup.file_history", false)
            .Read("storage.volume.free_pct", 15, "C:")
            .Advice());
        yield return ("two drives of one model, one checked again", () => new AdvicePc()
            .Disk(0, "system", "C:", 104.4).Disk(1, "internal", "D:")
            .Drive("smart.reallocated", 2, 0).Drive("smart.reallocated", 0, 1).Growth("smart.reallocated", 0, 2)
            .Advice());
        yield return ("failing drive of unknown role", () => NeedsARestart(new AdvicePc())
            .Drive("smart.reallocated", 5, 3, AdvicePc.DataDisk)
            .Info("backup.onedrive_folders", false).Read("backup.file_history", false).Read("backup.any_protection", true)
            .Read("storage.volume.free_pct", 8, "C:")
            .Advice());
        yield return ("no system drive known", () => new AdvicePc()
            .Disk(0, "system", "C:").Drive("smart.reallocated", 3)
            .Module("volumes", ModuleStatus.Failed)
            .Advice(systemDrive: null));
        yield return ("checks that broke", () => new AdvicePc()
            .Module("disk.smart", ModuleStatus.Failed).Module("volumes", ModuleStatus.Failed).Module("sensors", ModuleStatus.Failed)
            .Advice());
        yield return ("hot, speed test broke, scan not elevated", () => new AdvicePc()
            .Read("cpu.temperature_c", 91, AdvicePc.Cpu).Module("bench.cpu", ModuleStatus.Failed)
            .Module("disk.smart", null).Module("disk.reliability", null)
            .Advice());
        yield return ("hot, no speed test", () => new AdvicePc()
            .Read("cpu.temperature_c", 91, AdvicePc.Cpu).Module("bench.cpu", null)
            .Advice());
        yield return ("hot desktop, TPM and Secure Boot switched on since, no drive read", () => new AdvicePc()
            .Read("cpu.temperature_c", 91, AdvicePc.Cpu).Read("bench.cpu.clock_pct_of_base", 78, AdvicePc.Cpu)
            .Read("os.win11_ready", false).Info("os.win11_blockers", "Tpm SecureBoot")
            .Read("security.tpm_ready", true).Info("security.tpm_version", "2.0").Read("security.secure_boot", true)
            .Advice());
        yield return ("elevated, drive checks not selected", () => new AdvicePc()
            .Module("disk.smart", null).Module("disk.reliability", null)
            .Advice(ranElevated: true));
    }

    private static IEnumerable<string> KeysOf(AdviceSection section) =>
        section.Recommendations.SelectMany(c => c.Lines.Select(l => l.Key).Prepend(c.Title.Key))
            .Concat(section.NotMeasured.Select(n => n.Text.Key))
            .Concat(section.Notes.Select(n => n.Key));

    private static readonly string[] ItemPrefixes =
    [
        "advice.h1.", "advice.o4.", "advice.o1.", "advice.space.", "advice.o3.", "advice.s1.", "advice.s4.", "advice.not-measured.", "advice.note.",
    ];

    /// <summary>Text the plan carries for advice whose readings are Step C's: no item prints it yet.</summary>
    private static readonly string[] StepCKeys = ["advice.s4.hard-disk", "advice.s4.low-memory"];

    [Fact]
    public void Every_item_sentence_in_the_catalog_is_printed_by_some_fixture_with_every_value_it_names()
    {
        var printed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, work) in Tour())
        {
            var section = work();
            Assert.True(section.ItemErrors.Count == 0, $"{name}: {string.Join(" | ", section.ItemErrors.Select(e => e.Text))}");
            Assert.All(KeysOf(section), key => Assert.True(AdviceCatalog.English.Contains(key), key));
            Assert.DoesNotContain(section.Recommendations.SelectMany(c => Lines(c).Prepend(c.Title.Text)), t => t.Contains('{', StringComparison.Ordinal));
            printed.UnionWith(KeysOf(section));
        }

        var unprinted = AdviceCatalog.English.Keys
            .Where(k => ItemPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)) && !StepCKeys.Contains(k) && !printed.Contains(k))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(unprinted.Count == 0, "No fixture prints: " + string.Join(", ", unprinted));
    }

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("de-DE")]
    public void Every_fixture_reads_byte_for_byte_the_same_under_any_regional_format(string culture)
    {
        static string All() => string.Join("\n", Tour().Select(t => JsonSerializer.Serialize(t.Section())));
        var (saved, savedUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        string invariant, local;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            invariant = All();
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            local = All();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = savedUi;
        }

        Assert.Equal(invariant, local);
        Assert.Contains("\"Text\":\"About 104 GB is in use on C:", invariant, StringComparison.Ordinal);
    }
}
