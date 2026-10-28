using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcHealth.Core.Advice;
using PcHealth.Core.Advice.Items;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows.Support;
using Xunit;
using Xunit.Abstractions;

namespace PcHealth.Tests;

/// <summary>
/// Runs only where the three pilot laptops' audit zips are. The af47a36 build made exactly those
/// three. Elsewhere the test shows as skipped, never as passed: the zips are gitignored, because they
/// hold serial numbers and names, and they reach the dev T470p by hand.
/// </summary>
public sealed class PilotZipsFactAttribute : FactAttribute
{
    public PilotZipsFactAttribute()
    {
        if (!AuditResults.All.Any(r => r.IsPilot))
            Skip = "Replays the three pilot laptops' audit zips (the af47a36 build) in audit-results/, which git ignores. They are not on this machine.";
    }
}

/// <summary>Runs only where audit-results/ holds at least one zip; elsewhere skipped, never passed.</summary>
public sealed class AuditZipsFactAttribute : FactAttribute
{
    public AuditZipsFactAttribute()
    {
        if (AuditResults.All.Count == 0)
            Skip = "Replays the laptop audit zips in audit-results/, which git ignores. There are none on this machine.";
    }
}

/// <summary>
/// One laptop audit zip, as the replay needs it. Read in place and only in memory: nothing is
/// extracted or copied. It keeps no file name, serial, computer name or user name, and it prints as
/// its place in the folder only, so an assertion that shows it cannot show anything else.
/// </summary>
/// <param name="Number">Its place among the folder's zips, in file-name order.</param>
/// <param name="Error">Why it could not be read (the exception's type only), or null.</param>
/// <param name="HasReport">False for a run that left no report: there is nothing to replay.</param>
/// <param name="IsPilot">Made by the af47a36 build, which made only the three pilot zips.</param>
/// <param name="SystemDiskModel">machine.json's model of the disk Windows runs from. Never printed.</param>
/// <param name="ExternalDriveNames">disks.json's names of the drives on USB, 1394, SD or MMC. Never printed.</param>
internal sealed record AuditRun(
    int Number,
    string? Error,
    bool HasReport,
    bool IsPilot,
    bool RanElevated,
    IReadOnlyList<Metric> Metrics,
    IReadOnlyList<ModuleRunSummary> Modules,
    string? SystemDrive,
    string? SystemDiskModel,
    IReadOnlyList<string> ExternalDriveNames)
{
    public ModuleStatus? StatusOf(string moduleId) =>
        Modules.FirstOrDefault(m => string.Equals(m.ModuleId, moduleId, StringComparison.Ordinal))?.Status;

    public override string ToString() => "audit zip #" + Number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The zips in <c>audit-results/</c>, top level only (the folder's subfolders hold copies and runs
/// that are not fleet laptops). A zip that cannot be read is kept, with its error, so the replay of
/// every zip fails on it instead of leaving it out.
/// </summary>
internal static class AuditResults
{
    /// <summary>The kit's external buses (<c>audit-common.ps1</c>, <c>$script:ExternalBusTypes</c>).</summary>
    private static readonly string[] ExternalBusTypes = ["USB", "1394", "SD", "MMC"];

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    private static readonly Lazy<IReadOnlyList<AuditRun>> Runs = new(Load);

    public static IReadOnlyList<AuditRun> All => Runs.Value;

    private static IReadOnlyList<AuditRun> Load()
    {
        var folder = Path.Combine(AdviceCatalogTests.RepoRoot(), "audit-results");
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select((path, i) => Read(path, i + 1))];
    }

    private static AuditRun Read(string path, int number)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            ZipArchiveEntry? report = null, machine = null, disks = null;
            foreach (var entry in zip.Entries)
            {
                // The kit's zips name the report "report\<name>.json", with a backslash, and the
                // kit's own files with none: entry names are matched with either slash.
                var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var name = parts[^1];
                if (parts.Length >= 2 && string.Equals(parts[^2], "report", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) report ??= entry;
                else if (string.Equals(name, "machine.json", StringComparison.OrdinalIgnoreCase)) machine ??= entry;
                else if (string.Equals(name, "disks.json", StringComparison.OrdinalIgnoreCase)) disks ??= entry;
            }
            if (report is null) return new AuditRun(number, null, false, false, false, [], [], null, null, []);

            string? systemDrive = null, systemDiskModel = null;
            if (machine is not null)
            {
                using var document = Parse(machine);
                systemDrive = TextOf(document.RootElement, "SystemDrive");
                systemDiskModel = TextOf(document.RootElement, "SystemDiskModel");
            }

            var external = new List<string>();
            if (disks is not null)
            {
                using var document = Parse(disks);
                // ConvertTo-Json writes one disk as an object, several as an array.
                JsonElement[] list = document.RootElement.ValueKind == JsonValueKind.Array ? [.. document.RootElement.EnumerateArray()] : [document.RootElement];
                foreach (var disk in list)
                {
                    var bus = TextOf(disk, "BusType");
                    var friendly = TextOf(disk, "FriendlyName")?.Trim();
                    if (bus is not null && ExternalBusTypes.Contains(bus, StringComparer.Ordinal) && !string.IsNullOrEmpty(friendly)) external.Add(friendly);
                }
            }

            using var json = Parse(report);
            var root = json.RootElement;
            var modules = root.GetProperty("Modules").EnumerateArray()
                .Select(m => new ModuleRunSummary(
                    m.GetProperty("ModuleId").GetString()!,
                    m.GetProperty("ModuleName").GetString()!,
                    Enum.Parse<ModuleStatus>(m.GetProperty("Status").GetString()!),
                    TimeSpan.Zero, null, []))
                .ToList();
            var metrics = root.GetProperty("Verdicts").EnumerateArray()
                .Select(v => v.GetProperty("Metric").Deserialize<Metric>(Options)!)
                .ToList();
            var isPilot = TextOf(root, "AppVersion")?.Contains("+af47a36", StringComparison.Ordinal) == true;

            return new AuditRun(number, null, true, isPilot, root.GetProperty("RanElevated").GetBoolean(),
                metrics, modules, systemDrive, systemDiskModel, external);
        }
        catch (Exception e)
        {
            // Never the message: it can quote the file's name. A zip that could not be read must
            // fail the replay of every zip, not stop the tests from being found.
            return new AuditRun(number, e.GetType().Name, false, false, false, [], [], null, null, []);
        }
    }

    private static JsonDocument Parse(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    /// <summary>A property as text, the way PowerShell's <c>[string]</c> reads it; null when absent.</summary>
    private static string? TextOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText(),
            }
            : null;
}

/// <summary>One zip's report, re-graded and given the disk facts it lacks, and the advice for it.</summary>
/// <param name="Roles">Where the harness put each drive, by disk number, from the kit's own files.</param>
/// <param name="DriveSubjects">Every drive's Subject in the report, to check that no sentence names one. Never printed.</param>
internal sealed record Replay(
    AuditRun Run,
    IReadOnlyList<Verdict> Verdicts,
    IReadOnlyDictionary<int, DiskRole> Roles,
    IReadOnlyList<string> DriveSubjects,
    AdviceSection Advice)
{
    public IEnumerable<int> DisksWith(DiskRole role) => Roles.Where(r => r.Value == role).Select(r => r.Key);

    /// <summary>"H1 ActNow", "O4 Advice": what a card is, without its Subject.</summary>
    public IReadOnlyList<string> Cards => [.. Advice.Recommendations.Select(c => c.Item + " " + c.Urgency)];

    public override string ToString() => Run.ToString();
}

/// <summary>
/// The pilot replay's harness (STEP-B-SPEC 7). A pilot-era report has no disk numbers and no disk
/// role rows: the af47a36 build wrote neither. The harness supplies them the way today's volume check
/// would, from the kit's own files in the same zip, and changes nothing else: the engine is the
/// shipped one.
/// <list type="bullet">
/// <item>Each drive reading's Subject without a disk number gets a synthetic one, 100 and up.</item>
/// <item>Each drive gets a <c>storage.disk.role</c> row: <c>system</c> when its Subject is machine.json's
/// system disk model, <c>external</c> when disks.json lists it on USB, 1394, SD or MMC, otherwise
/// <c>internal</c>. That is the app's own order (the disk Windows runs from is the PC's own, whatever its
/// connection), and the names match as the kit matches them (<c>Test-ExternalDrive</c>).</item>
/// <item>The drive Windows runs from comes from machine.json, and whether the scan was elevated from the
/// report, as BuildReport passes them. There are no trends: the kit runs with
/// <c>--no-history</c>.</item>
/// <item>Every reading is graded again by today's Windows rules. Only the rules are today's: whether a
/// reading is information only, and its id, stay as the old build wrote them, so this proves the
/// advice and not a module's fix (TEST-002).</item>
/// </list>
/// A report that already has disk numbers or role rows (later builds) keeps them.
/// </summary>
internal static class PilotReplay
{
    public const int FirstSyntheticDisk = 100;

    private static readonly VerdictEngine Rules = new(RulesLoader.LoadEmbedded("windows"));

    /// <param name="tagExternal">False writes no role row for a USB drive, as when the volume check
    /// was not part of the scan, to show what the role row is for.</param>
    public static Replay Run(AuditRun run, bool tagExternal = true)
    {
        Assert.True(run.HasReport, $"{run} has no report to replay");

        var synthetic = run.Metrics
            .Where(m => m.DiskNumber is null && IsDriveReading(m) && !string.IsNullOrWhiteSpace(m.Subject))
            .Select(m => m.Subject!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select((subject, i) => (Subject: subject, Disk: FirstSyntheticDisk + i))
            .ToDictionary(p => p.Subject, p => p.Disk, StringComparer.Ordinal);
        var metrics = run.Metrics
            .Select(m => m.DiskNumber is null && m.Subject is { } s && synthetic.TryGetValue(s, out var disk) ? m with { DiskNumber = disk } : m)
            .ToList();

        var roles = metrics
            .Where(m => m.DiskNumber is not null && IsDriveReading(m) && !string.IsNullOrWhiteSpace(m.Subject))
            .GroupBy(m => m.DiskNumber!.Value)
            .ToDictionary(g => g.Key, g => RoleOf([.. g.Select(m => m.Subject!).Distinct(StringComparer.Ordinal)], run));
        foreach (var (disk, role) in roles.OrderBy(r => r.Key))
        {
            if (role == DiskRole.External && !tagExternal) continue;
            if (metrics.Exists(m => m.Id == AdviceFacts.DiskRoleId && m.DiskNumber == disk)) continue;
            var text = role switch
            {
                DiskRole.System => DiskRoles.SystemRole,
                DiskRole.External => DiskRoles.ExternalRole,
                _ => DiskRoles.InternalRole,
            };
            metrics.Add(new Metric(AdviceFacts.DiskRoleId, "Disk role", text, HealthCategory.Storage,
                Subject: "Disk " + disk.ToString(CultureInfo.InvariantCulture), InfoOnly: true, DiskNumber: disk));
        }

        var verdicts = Rules.Evaluate(metrics);
        var advice = AdviceEngine.ForOs("windows").Derive(verdicts, [], run.Modules,
            new AdviceContext(AdviceContext.NormalizeDrive(run.SystemDrive), run.RanElevated));
        Assert.NotNull(advice);

        var subjects = metrics.Where(IsDriveReading).Select(m => m.Subject).OfType<string>()
            .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return new Replay(run, verdicts, roles, subjects, advice);
    }

    /// <summary>A reading the drive checks write about one drive: SMART, and the damage and wear readings H1 reads.</summary>
    private static bool IsDriveReading(Metric metric) =>
        metric.Id.StartsWith("smart.", StringComparison.Ordinal) || AdviceFacts.DriveReadingIds.Contains(metric.Id);

    private static DiskRole RoleOf(IReadOnlyList<string> subjects, AuditRun run) =>
        run.SystemDiskModel is { } model && subjects.Any(s => IsNamed(s, model)) ? DiskRole.System
        : subjects.Count > 0 && subjects.All(s => run.ExternalDriveNames.Any(name => IsNamed(s, name))) ? DiskRole.External
        : DiskRole.Internal;

    /// <summary>
    /// The kit's <c>Test-ExternalDrive</c>: the name itself, or the name followed by the app's
    /// separator (" — 240 GB"), never the name alone as a prefix, so "Samsung SSD" is not
    /// "Samsung SSD Pro".
    /// </summary>
    private static bool IsNamed(string subject, string name)
    {
        var trimmed = subject.Trim();
        name = name.Trim();
        return name.Length > 0 &&
               (string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(name + " —", StringComparison.Ordinal));
    }
}

/// <summary>
/// The pilot replay (STEP-B-SPEC 7, REMEDIES-PLAN principle 14): the three pilot laptops' reports,
/// re-graded by today's rules and read by today's advice engine. Each must get exactly the advice its
/// readings call for: "back up now" for the T470p's failing SSD, "not measured" where a drive check
/// broke, and nothing about the auditor's own USB drive, whose read errors are still graded Bad.
///
/// The zips are read in place and never copied. Nothing here prints a file name, a Subject (a
/// drive's model), a computer name or a user name: messages name a zip by its place in the folder,
/// and a card by its item and urgency.
/// </summary>
public sealed class PilotReplayTests(ITestOutputHelper output)
{
    private static (Replay T470p, Replay T470sElevated, Replay T470sStandard) ThePilots()
    {
        var pilots = AuditResults.All.Where(r => r.IsPilot).ToList();
        Assert.True(pilots.Count == 3, $"Expected the three pilot zips (af47a36), found {pilots.Count}.");
        // The harness is for pilot-era reports: none of them has a disk number or a role row.
        Assert.All(pilots, p => Assert.DoesNotContain(p.Metrics, m => m.DiskNumber is not null || m.Id == AdviceFacts.DiskRoleId));

        // Told apart by what they hold, never by their file names.
        var t470p = pilots.Where(p => p.RanElevated && p.StatusOf(DriveHealthItem.SmartModule) == ModuleStatus.Completed).ToList();
        var t470sElevated = pilots.Where(p => p.RanElevated && p.StatusOf(DriveHealthItem.SmartModule) == ModuleStatus.Failed).ToList();
        var t470sStandard = pilots.Where(p => !p.RanElevated).ToList();
        Assert.True(t470p.Count == 1 && t470sElevated.Count == 1 && t470sStandard.Count == 1,
            "Expected one elevated pilot whose SMART check finished, one elevated pilot whose SMART check failed, and one standard-user pilot.");
        return (PilotReplay.Run(t470p[0]), PilotReplay.Run(t470sElevated[0]), PilotReplay.Run(t470sStandard[0]));
    }

    private void Show(string label, Replay replay)
    {
        var section = replay.Advice;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label} ({replay}): cards [{string.Join(", ", replay.Cards)}]; not measured [{string.Join(", ", section.NotMeasured.Select(n => n.Area))}]; " +
            $"notes [{string.Join(", ", section.Notes.Select(n => n.Key))}]; other findings {section.OtherFindings}; item errors {section.ItemErrors.Count}"));
    }

    [PilotZipsFact]
    public void The_T470p_with_5_retired_sectors_gets_one_back_up_now_card_about_the_drive_Windows_runs_from()
    {
        var (replay, _, _) = ThePilots();
        Show("T470p, elevated", replay);
        var section = replay.Advice;

        Assert.Equal(["H1 ActNow"], replay.Cards);
        var card = section.Recommendations[0];
        Assert.Equal("advice.h1.title.act-now.system", card.Title.Key);
        Assert.Equal(Grade.Bad, card.Severity);
        Assert.True(card.DiskNumber is { } disk && replay.Roles[disk] == DiskRole.System, "The card is not on the drive the harness tagged as the system disk.");

        var keys = card.Lines.Select(l => l.Key).ToList();
        Assert.Equal("C:", card.Lines.Single(l => l.Key == "advice.h1.role.system").Args["drive"].Text);
        Assert.Equal(5, card.Lines.Single(l => l.Key == "advice.h1.evidence.smart.reallocated").Args["count"].Number);
        Assert.Contains(card.Evidence, e => e.MetricId == "smart.reallocated" && e.Grade == Grade.Bad);
        // A pilot-era report has no space-in-use row: no estimate, and the replacement size is the
        // "1.5 times the space in use" sentence with no number in it, never "0 GB".
        Assert.DoesNotContain("advice.h1.do.estimate", keys);
        Assert.Contains("advice.h1.for-it.replace.unmeasured", keys);

        var text = string.Join("\n", card.Lines.Select(l => l.Text).Prepend(card.Title.Text));
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
        Assert.False(replay.DriveSubjects.Any(s => text.Contains(s, StringComparison.Ordinal)), "A sentence names a drive's model.");

        Assert.Empty(section.NotMeasured.Select(n => n.Area));
        Assert.Empty(section.Notes.Select(n => n.Key));
        Assert.Empty(section.ItemErrors.Select(e => e.Text));
    }

    [PilotZipsFact]
    public void The_elevated_T470s_whose_SMART_check_failed_gets_no_card_and_says_drive_health_was_not_fully_checked()
    {
        var (_, replay, _) = ThePilots();
        Show("T470s, elevated", replay);
        var section = replay.Advice;

        Assert.Empty(replay.Cards);
        var line = Assert.Single(section.NotMeasured);
        Assert.Equal("drive-health", line.Area);
        Assert.Equal([DriveHealthItem.SmartModule], line.ModuleIds);
        Assert.Equal("advice.not-measured.drive-health", line.Text.Key);
        Assert.Empty(section.Notes.Select(n => n.Key));
        Assert.Empty(section.ItemErrors.Select(e => e.Text));
    }

    [PilotZipsFact]
    public void The_standard_user_T470s_gets_no_card_and_says_drive_health_and_heat_were_not_measured()
    {
        var (_, _, replay) = ThePilots();
        Show("T470s, standard user", replay);
        var section = replay.Advice;

        Assert.Empty(replay.Cards);
        Assert.Equal(["drive-health", "heat"], section.NotMeasured.Select(n => n.Area));
        Assert.Equal([DriveHealthItem.SmartModule, DriveHealthItem.ReliabilityModule], section.NotMeasured[0].ModuleIds);
        Assert.Equal([HotAndSlowItem.SensorsModule], section.NotMeasured[1].ModuleIds);
        // Every administrator check failed here, which is "tried and broke", not "not part of this
        // scan": amber lines, and no grey note saying the checks were left out.
        Assert.Empty(section.Notes.Select(n => n.Key));
        Assert.Empty(section.ItemErrors.Select(e => e.Text));
    }

    [PilotZipsFact]
    public void No_pilot_gets_a_card_about_the_auditors_USB_drive_although_its_read_errors_are_still_graded_Bad()
    {
        var (t470p, t470sElevated, t470sStandard) = ThePilots();
        Replay[] all = [t470p, t470sElevated, t470sStandard];

        // The reading is there and Bad on two of the three, as the pilot found it, so the silence
        // below is the role row's doing, not a missing reading.
        var withBadUsbErrors = all.Where(r => r.Verdicts.Any(v =>
            v.Metric.Id == "storage.read_errors_uncorrected" && v.Grade == Grade.Bad &&
            v.Metric.DiskNumber is { } disk && r.Roles[disk] == DiskRole.External)).ToList();
        Assert.Equal(2, withBadUsbErrors.Count);

        foreach (var replay in all)
        {
            var external = replay.DisksWith(DiskRole.External).ToHashSet();
            Assert.DoesNotContain(replay.Advice.Recommendations, c => c.DiskNumber is { } disk && external.Contains(disk));
            // And none of the cards the plan's other items could make: the only free space under 20%
            // is on a USB volume, Windows 11 is blocked by the processor on all three, and each had
            // been restarted within a day.
            Assert.DoesNotContain(replay.Advice.Recommendations, c => c.Item is "SPACE" or "O1" or "S1" or "O3" or "O4");
        }

        // The af47a36 volume check still graded a USB volume's free space (today's writes it as
        // information only, STOR-001), so the low USB volume is a graded Normal in these reports. The
        // space card stays silent because it reads the drive Windows runs from only (TEST-002).
        Assert.All(all, r => Assert.Contains(r.Verdicts, v =>
            v.Metric.Id == AdviceFacts.FreeSpaceId && v.Grade == Grade.Normal && !v.Metric.InfoOnly &&
            v.Metric.Subject?.StartsWith(r.Run.SystemDrive ?? "C:", StringComparison.OrdinalIgnoreCase) == false));
    }

    private static readonly string[] DriveChecks = [DriveHealthItem.SmartModule, DriveHealthItem.ReliabilityModule, "volumes"];

    /// <summary>A pilot-era report with drive readings and the kit's files, built here, every drive check Completed.</summary>
    private static AuditRun Synthetic(string systemDiskModel, IReadOnlyList<string> externalNames, params (string Id, double Value, string Subject)[] readings) =>
        new(0, null, true, false, true,
            [.. readings.Select(r => new Metric(r.Id, r.Id, r.Value, HealthCategory.Storage, Subject: r.Subject))],
            [.. DriveChecks.Select(id => new ModuleRunSummary(id, id, ModuleStatus.Completed, TimeSpan.Zero, null, []))],
            "C:", systemDiskModel, externalNames);

    private static int DiskOf(Replay replay, string subject) =>
        replay.Verdicts.First(v => v.Metric.Subject == subject && v.Metric.DiskNumber is not null).Metric.DiskNumber!.Value;

    [Fact]
    public void The_harness_puts_the_drive_Windows_runs_from_first_even_on_USB_as_the_volume_check_does()
    {
        // Windows started from a USB SSD: the kit lists the drive on USB, and it is also the system
        // disk. The volume check calls it "system" (DiskRoles.From), so the harness must too, or
        // the replay would show a silence the app would not (ADV-008).
        var replay = PilotReplay.Run(Synthetic("MODEL-U", ["MODEL-U"], ("smart.reallocated", 5, "MODEL-U — 500 GB")));

        Assert.Equal(DiskRole.System, replay.Roles[DiskOf(replay, "MODEL-U — 500 GB")]);
        Assert.Equal(["H1 ActNow"], replay.Cards);
        Assert.Equal("advice.h1.title.act-now.system", replay.Advice.Recommendations[0].Title.Key);
    }

    [Fact]
    public void The_harness_matches_drive_names_as_the_kit_does_so_a_longer_model_name_is_another_drive()
    {
        // The kit's Test-ExternalDrive: the name, or the name and the separator. "MODEL-X" on USB is
        // not "MODEL-X Pro" inside the PC.
        var replay = PilotReplay.Run(Synthetic("MODEL-Y", ["MODEL-X"],
            ("smart.reallocated", 5, "MODEL-X Pro — 1000 GB"),
            ("storage.read_errors_uncorrected", 2, "MODEL-X — 64 GB")));

        var inside = DiskOf(replay, "MODEL-X Pro — 1000 GB");
        Assert.Equal(DiskRole.Internal, replay.Roles[inside]);
        Assert.Equal(DiskRole.External, replay.Roles[DiskOf(replay, "MODEL-X — 64 GB")]);
        var card = Assert.Single(replay.Advice.Recommendations);
        Assert.Equal(inside, card.DiskNumber);
        Assert.Equal("advice.h1.title.act-now.internal", card.Title.Key);
        Assert.Empty(replay.Advice.ItemErrors);
    }

    [PilotZipsFact]
    public void Without_the_disk_role_row_the_USB_drive_would_get_a_back_up_now_card_that_says_it_may_be_a_USB_drive()
    {
        // STEP-B-SPEC risk 1, the other direction: what the volume check's role row prevents. A drive
        // with no role row counts as inside the PC (KIT-003), so the auditor's drive gets a card, and
        // the card says it may be about a USB drive.
        var pilots = AuditResults.All.Where(r => r.IsPilot).Select(r => PilotReplay.Run(r, tagExternal: false)).ToList();

        var usbCards = pilots
            .SelectMany(r => r.Advice.Recommendations.Where(c => c.Item == DriveHealthItem.ItemId && c.DiskNumber is { } disk && r.Roles[disk] == DiskRole.External))
            .ToList();
        Assert.Equal(2, usbCards.Count);
        Assert.All(usbCards, c =>
        {
            Assert.Equal(AdviceUrgency.ActNow, c.Urgency);
            Assert.Equal("advice.h1.title.act-now.unknown", c.Title.Key);
            Assert.Contains(c.Lines, l => l.Key == "advice.h1.role.unknown");
        });
    }

    [AuditZipsFact]
    public void Every_audit_zip_replays_with_each_card_resting_on_a_graded_problem_and_none_on_a_USB_drive()
    {
        var runs = AuditResults.All;
        Assert.All(runs, r => Assert.True(r.Error is null, $"{r} could not be read ({r.Error})."));

        foreach (var run in runs)
        {
            if (!run.HasReport)
            {
                output.WriteLine($"{run}: no report inside, nothing to replay");
                continue;
            }

            var replay = PilotReplay.Run(run);
            Show(run.IsPilot ? "pilot" : "fleet", replay);
            Assert.Empty(replay.Advice.ItemErrors.Select(e => e.Text));

            var external = replay.DisksWith(DiskRole.External).ToHashSet();
            foreach (var card in replay.Advice.Recommendations)
            {
                // Checked here without the engine's own helpers: one piece of evidence is a reading in
                // this report, graded Normal or Bad, and not information only.
                Assert.True(card.Evidence.Any(e => replay.Verdicts.Any(v =>
                        v.Metric.Id == e.MetricId && v.Metric.Subject == e.Subject && v.Metric.DiskNumber == e.DiskNumber &&
                        v.Grade == e.Grade && !v.Metric.InfoOnly && v.Grade is Grade.Normal or Grade.Bad)),
                    $"{run}: the {card.Item} {card.Urgency} card rests on no graded problem.");
                Assert.False(card.Item == DriveHealthItem.ItemId && card.DiskNumber is { } disk && external.Contains(disk),
                    $"{run}: an H1 card is about a drive the kit's own disk list puts on USB.");
            }
        }
    }
}
