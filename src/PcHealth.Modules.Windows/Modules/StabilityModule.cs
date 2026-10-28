using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>Windows Reliability Monitor stability index (1–10).</summary>
internal sealed class StabilityModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "stability",
        Name = "Reliability index",
        Description = "Windows' own 1–10 stability index from Reliability Monitor, plus its 30-day trend.",
        Reads = ["Reliability metrics, and how far back they go (WMI)", "When Windows was installed, only if the history is empty (WMI)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(5),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var rows = Wmi.TryQuery(@"root\cimv2",
            "SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics", out var error);
        if (error is not null)
        {
            notes.Add($"Reliability metrics unavailable: {error}");
            return Task.FromResult(ModuleStatus.PartiallyCompleted);
        }
        if (rows.Count == 0)
        {
            var (status, note) = ReliabilityHistory.WhenEmpty(InstallAge.ReadYoung("the empty reliability history", notes));
            notes.Add(note);
            return Task.FromResult(status);
        }

        // CIM datetime strings sort lexicographically — newest row without materializing DateTimes for all.
        var newest = rows.MaxBy(r => Wmi.GetString(r, "TimeGenerated") ?? "");
        var latestIndex = newest is null ? null : Wmi.Get<double>(newest, "SystemStabilityIndex");
        if (latestIndex is { } index)
        {
            var cutoff = DateTime.Now.AddDays(-30);
            var readings = rows
                .Select(r => (Index: Wmi.Get<double>(r, "SystemStabilityIndex"), Date: Wmi.GetCimDate(r, "TimeGenerated")))
                .ToList();
            var last30 = readings
                .Where(x => x.Index is not null && x.Date > cutoff)
                .Select(x => x.Index!.Value)
                .ToList();

            // The range is over as many days as the history holds, which after a reinstall is not 30 (M1).
            var oldest = readings.Min(x => x.Date);
            var span = RecordSpan.FromOldest("Reliability Monitor's history", oldest?.ToUniversalTime(), DateTime.UtcNow)
                .Describe(30);
            // Invariant, so a decimal-comma PC does not write "4,5–10,0" (REP-002).
            var detail = last30.Count == 0 ? "10 = rock solid, 1 = constant failures."
                : string.Create(CultureInfo.InvariantCulture,
                    $"{(span.Length == 0 ? "30-day range" : span + " Range")}: {last30.Min():0.0}–{last30.Max():0.0} " +
                    $"(average {last30.Average():0.0}). 10 = rock solid, 1 = constant failures.");
            metrics.Add(new Metric("stability.index", "System stability index", Math.Round(index, 1),
                HealthCategory.Stability, Detail: detail));
        }

        return Task.FromResult(metrics.Count > 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted);
    }
}
