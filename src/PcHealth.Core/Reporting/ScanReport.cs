using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;
using PcHealth.Core.Verdicts;

namespace PcHealth.Core.Reporting;

public sealed record ModuleRunSummary(
    string ModuleId,
    string ModuleName,
    ModuleStatus Status,
    TimeSpan Elapsed,
    string? Error,
    IReadOnlyList<string> Notes);

public sealed record ScanReport
{
    public string SchemaVersion { get; init; } = "1.0";
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string MachineName { get; init; }
    public required string OsDescription { get; init; }
    public required string AppVersion { get; init; }
    public required bool RanElevated { get; init; }

    public required IReadOnlyList<ModuleRunSummary> Modules { get; init; }
    public required IReadOnlyList<Verdict> Verdicts { get; init; }
    public required IReadOnlyList<ReceiptEntry> Receipt { get; init; }

    /// <summary>What changed since earlier scans. Empty on a first run.</summary>
    public IReadOnlyList<TrendFinding> Trends { get; init; } = [];

    /// <summary>How much history the trends are based on, so the report can be honest about it.</summary>
    public HistoryCoverage? History { get; init; }

    public required IReadOnlyDictionary<HealthCategory, Grade> CategoryGrades { get; init; }
    public required Grade OverallGrade { get; init; }

    public static Grade Worst(IEnumerable<Grade> grades)
    {
        var worst = Grade.Unknown;
        foreach (var g in grades)
        {
            if (g == Grade.Bad) return Grade.Bad;
            if (g == Grade.Normal) worst = Grade.Normal;
            else if (g == Grade.Good && worst == Grade.Unknown) worst = Grade.Good;
        }
        return worst;
    }

    public static IReadOnlyDictionary<HealthCategory, Grade> ComputeCategoryGrades(IEnumerable<Verdict> verdicts)
    {
        var result = new Dictionary<HealthCategory, Grade>();
        foreach (var group in verdicts.GroupBy(v => v.Metric.Category))
        {
            result[group.Key] = Worst(group.Select(v => v.Grade));
        }
        return result;
    }
}
