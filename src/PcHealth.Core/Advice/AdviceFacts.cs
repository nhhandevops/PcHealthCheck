using System.Globalization;
using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;

namespace PcHealth.Core.Advice;

/// <summary>
/// What advice needs to know about the PC that no reading says. <see cref="SystemDrive"/> is the
/// drive Windows runs from, as "C:", or null when not known. <see cref="RanElevated"/> is the
/// report's own: whether the scan had administrator rights, so a check left out of an elevated
/// scan is not blamed on rights it had.
/// </summary>
public sealed record AdviceContext(string? SystemDrive, bool RanElevated = false)
{
    /// <summary>The PC this process runs on: the root of its Windows folder, as the drive map reads it.</summary>
    public static AdviceContext ForThisPc(bool ranElevated) =>
        new(NormalizeDrive(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))), ranElevated);

    /// <summary>"C", "c:" and "C:\" all become "C:"; anything that is not one letter becomes null.</summary>
    public static string? NormalizeDrive(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var letter = raw.Trim().TrimEnd(':', '\\', '/');
        return letter.Length == 1 && char.IsAsciiLetter(letter[0])
            ? letter.ToUpperInvariant() + ":"
            : null;
    }
}

/// <summary>
/// Where a disk is, from the information-only <c>storage.disk.role</c> row. A disk with no such row
/// is <see cref="Unknown"/>, which advice treats as inside the PC: missing a failing internal drive
/// costs more than speaking about an external one (KIT-003).
/// </summary>
public enum DiskRole
{
    System,
    Internal,
    External,
    Unknown,
}

/// <summary>
/// The readings of one physical drive. <see cref="DiskNumber"/> is Windows' number for it; a group
/// without one holds drive readings that named no disk, gathered by their Subject, and its role is
/// always <see cref="DiskRole.Unknown"/>.
/// </summary>
/// <param name="Key">"disk0" for disk 0; "unknown-1", "unknown-2"... for groups gathered by Subject, numbered in Subject order.</param>
/// <param name="Subject">The drive's own name, model and size, from its readings. Never printed in a sentence.</param>
/// <param name="Letters">The drive letters on the disk ("C:, D:"), from the role row; null when it has none or there is no row.</param>
/// <param name="UsedGb">The space in use on its lettered drives, from <c>storage.disk.used_gb</c>; null when not measured.</param>
/// <param name="Trends">The trends on its readings, when no other drive in this scan has a reading with the same id and Subject.</param>
/// <param name="SharesHistory">
/// Another drive in this scan has one of its drive readings under the same id and Subject, as a
/// second drive of the same model does. History keeps one series for both, so no trend can be told
/// to be this drive's, and scanning again will not tell either.
/// </param>
public sealed record DiskGroup(
    string Key,
    int? DiskNumber,
    string? Subject,
    DiskRole Role,
    string? Letters,
    double? UsedGb,
    IReadOnlyList<Verdict> Verdicts,
    IReadOnlyList<TrendFinding> Trends,
    bool SharesHistory);

/// <summary>
/// The report's readings, indexed once for every advice item (STEP-B-SPEC 3.3). Pure: built from the
/// report's own records, with no access to the PC.
/// </summary>
public sealed class AdviceFacts
{
    public const string DiskRoleId = "storage.disk.role";
    public const string DiskUsedGbId = "storage.disk.used_gb";
    public const string FreeSpaceId = "storage.volume.free_pct";

    /// <summary>
    /// Drive readings that are gathered by their Subject when they name no disk. They are the readings
    /// of damage the disk benchmark also refuses to write to (<see cref="DriveDamage"/>, shared with
    /// <c>FailingDrives</c> in the Windows modules), and the two wear readings.
    /// </summary>
    public static readonly IReadOnlySet<string> DriveReadingIds = new HashSet<string>(DriveDamage.MetricIds, StringComparer.Ordinal)
    {
        "storage.wear_pct", "smart.ssd_percent_consumed",
    };

    private readonly Dictionary<string, List<Verdict>> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(string MetricId, string? Subject), List<Verdict>> _bySubject = new();
    private readonly Dictionary<int, List<Verdict>> _byDisk = new();
    private readonly Dictionary<(string MetricId, string? Subject), List<TrendFinding>> _trends = new();
    private readonly Dictionary<string, ModuleRunSummary> _modules = new(StringComparer.Ordinal);

    private AdviceFacts(
        IReadOnlyList<Verdict> verdicts, IReadOnlyList<TrendFinding> trends,
        IReadOnlyList<ModuleRunSummary> modules, AdviceContext context)
    {
        Verdicts = verdicts;
        Trends = trends;
        Modules = modules;
        Context = context;

        foreach (var verdict in verdicts)
        {
            Add(_byId, verdict.Metric.Id, verdict);
            Add(_bySubject, (verdict.Metric.Id, verdict.Metric.Subject), verdict);
            if (verdict.Metric.DiskNumber is { } disk) Add(_byDisk, disk, verdict);
        }
        foreach (var trend in trends) Add(_trends, (trend.MetricId, trend.Subject), trend);
        foreach (var module in modules) _modules.TryAdd(module.ModuleId, module);

        DiskGroups = GroupDisks();
        SystemVolume = context.SystemDrive is { } drive
            ? verdicts.FirstOrDefault(v => v.Metric.Id == FreeSpaceId && !v.Metric.InfoOnly && IsOnDrive(v.Metric.Subject, drive))
            : null;
    }

    public static AdviceFacts Build(
        IReadOnlyList<Verdict> verdicts, IReadOnlyList<TrendFinding> trends,
        IReadOnlyList<ModuleRunSummary> modules, AdviceContext context) =>
        new(verdicts, trends, modules, context);

    public AdviceContext Context { get; }

    public IReadOnlyList<Verdict> Verdicts { get; }

    public IReadOnlyList<TrendFinding> Trends { get; }

    public IReadOnlyList<ModuleRunSummary> Modules { get; }

    /// <summary>Every physical drive with readings, by disk number, then the groups gathered by Subject.</summary>
    public IReadOnlyList<DiskGroup> DiskGroups { get; }

    /// <summary>
    /// The graded free-space reading of the drive Windows runs from: its Subject is the system drive,
    /// or the system drive and a space and its label ("C: (Windows)"). Only the drive letter is ever
    /// printed from it, never its Subject.
    /// </summary>
    public Verdict? SystemVolume { get; }

    public IReadOnlyList<Verdict> All(string metricId) => _byId.GetValueOrDefault(metricId) ?? [];

    public IReadOnlyList<Verdict> All(string metricId, string? subject) => _bySubject.GetValueOrDefault((metricId, subject)) ?? [];

    /// <summary>The first reading with this id, for the ones a scan makes once (uptime, a pending restart).</summary>
    public Verdict? First(string metricId) => All(metricId) is [var first, ..] ? first : null;

    /// <summary>The readings with this id graded Normal or Bad. An information-only row is never one.</summary>
    public IEnumerable<Verdict> Problems(string metricId) => All(metricId).Where(AdviceEvidence.IsProblem);

    /// <summary>A yes/no reading's value, graded or information only, or null when it is not in this report.</summary>
    public bool? Flag(string metricId) => First(metricId)?.Metric.Value.Flag;

    public IReadOnlyList<Verdict> OnDisk(int diskNumber) => _byDisk.GetValueOrDefault(diskNumber) ?? [];

    public IReadOnlyList<TrendFinding> TrendsOf(string metricId, string? subject) => _trends.GetValueOrDefault((metricId, subject)) ?? [];

    public ModuleRunSummary? Module(string moduleId) => _modules.GetValueOrDefault(moduleId);

    /// <summary>The module's status, or null when it was not part of this scan.</summary>
    public ModuleStatus? StatusOf(string moduleId) => Module(moduleId)?.Status;

    /// <summary>Whether a card's piece of evidence is a reading or trend in this report, graded as the card says.</summary>
    public bool Holds(AdviceEvidence evidence) => Find(evidence) is not null;

    /// <summary>Whether a card's piece of evidence is a graded problem in this report (STEP-B-SPEC invariant 1).</summary>
    public bool IsProblemEvidence(AdviceEvidence evidence) => Find(evidence) switch
    {
        Verdict verdict => AdviceEvidence.IsProblem(verdict),
        TrendFinding trend => AdviceEvidence.IsProblem(trend),
        _ => false,
    };

    private object? Find(AdviceEvidence evidence) => evidence.Trend is { } kind
        ? TrendsOf(evidence.MetricId, evidence.Subject).FirstOrDefault(t => t.Kind == kind && t.Grade == evidence.Grade)
        : All(evidence.MetricId, evidence.Subject).FirstOrDefault(v => v.Metric.DiskNumber == evidence.DiskNumber && v.Grade == evidence.Grade);

    /// <summary>"C:" is on drive "C:", and so is "C: (Windows)"; "CD:" and "D:" are not.</summary>
    private static bool IsOnDrive(string? subject, string drive) =>
        subject is not null &&
        (string.Equals(subject, drive, StringComparison.OrdinalIgnoreCase) ||
         subject.StartsWith(drive + " ", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One group per disk number, holding every reading that names it. A drive reading that names no
    /// disk joins a group for its Subject. A trend names no disk either: it joins the drive whose
    /// reading has its id and Subject, and only when no other drive has one. History keeps one series
    /// per id and Subject (<see cref="HistoryStore.RecordScan"/>), so two drives of one model write
    /// to the same series, and <see cref="TrendAnalyzer"/> compares a reading with the last sample
    /// stored, which may be the other drive's: 2 retired areas on one and none on the other read as
    /// "grown from 0 to 2" on every scan. Such a trend joins no drive and is left to its own row.
    /// </summary>
    private List<DiskGroup> GroupDisks()
    {
        var subjectVerdicts = new Dictionary<string, List<Verdict>>(StringComparer.Ordinal);
        var trendsOf = new Dictionary<string, List<TrendFinding>>(StringComparer.Ordinal);
        var bySubject = new SortedDictionary<string, string>(StringComparer.Ordinal); // Subject -> provisional key
        string? GroupOf(Verdict v) =>
            v.Metric.DiskNumber is { } disk ? DiskKey(disk)
            : DriveReadingIds.Contains(v.Metric.Id) ? SubjectKey(v.Metric.Subject)
            : null;
        string SubjectKey(string? subject)
        {
            var name = subject ?? "";
            if (!bySubject.TryGetValue(name, out var key)) bySubject[name] = key = "subject:" + name;
            return key;
        }

        foreach (var verdict in Verdicts.Where(v => v.Metric.DiskNumber is null))
        {
            if (GroupOf(verdict) is { } key) Add(subjectVerdicts, key, verdict);
        }

        // The drives with a reading of each id and Subject: history keeps one series for all of them.
        var drivesOf = Verdicts.Where(v => GroupOf(v) is not null)
            .GroupBy(v => (v.Metric.Id, v.Metric.Subject))
            .ToDictionary(g => g.Key, g => g.Select(v => GroupOf(v)!).Distinct(StringComparer.Ordinal).ToList());
        bool SharesHistory(IEnumerable<Verdict> readings) =>
            readings.Any(v => DriveReadingIds.Contains(v.Metric.Id) && drivesOf[(v.Metric.Id, v.Metric.Subject)].Count > 1);

        foreach (var trend in Trends)
        {
            // Not a drive's trend, or not one drive's: it joins none.
            if (drivesOf.GetValueOrDefault((trend.MetricId, trend.Subject)) is [var only]) Add(trendsOf, only, trend);
        }

        var result = new List<DiskGroup>();
        foreach (var (disk, readings) in _byDisk.OrderBy(d => d.Key))
        {
            var role = readings.FirstOrDefault(v => v.Metric.Id == DiskRoleId);
            var drive = readings.FirstOrDefault(v => v.Metric.Id is not (DiskRoleId or DiskUsedGbId));
            result.Add(new DiskGroup(
                DiskKey(disk), disk, drive?.Metric.Subject, RoleOf(role), LettersOf(role?.Metric.Subject),
                readings.FirstOrDefault(v => v.Metric.Id == DiskUsedGbId)?.Metric.Value.Number,
                readings, trendsOf.GetValueOrDefault(DiskKey(disk)) ?? [], SharesHistory(readings)));
        }

        var number = 0;
        foreach (var (subject, key) in bySubject)
        {
            number++;
            var readings = subjectVerdicts.GetValueOrDefault(key) ?? [];
            result.Add(new DiskGroup(
                "unknown-" + number.ToString(CultureInfo.InvariantCulture), null, subject.Length == 0 ? null : subject,
                DiskRole.Unknown, null, null,
                readings, trendsOf.GetValueOrDefault(key) ?? [], SharesHistory(readings)));
        }
        return result;
    }

    private static string DiskKey(int disk) => "disk" + disk.ToString(CultureInfo.InvariantCulture);

    private static DiskRole RoleOf(Verdict? role) => role?.Metric.Value.Text?.Trim().ToUpperInvariant() switch
    {
        "SYSTEM" => DiskRole.System,
        "INTERNAL" => DiskRole.Internal,
        "EXTERNAL" => DiskRole.External,
        _ => DiskRole.Unknown,
    };

    /// <summary>The role row's Subject when it lists drive letters ("C:, D:"), and null when it is "Disk 3".</summary>
    private static string? LettersOf(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var parts = subject.Split(',', StringSplitOptions.TrimEntries);
        return parts.All(p => p.Length == 2 && char.IsAsciiLetter(p[0]) && p[1] == ':') ? subject : null;
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> index, TKey key, TValue value) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(value);
    }
}
