using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>Devices reporting Device Manager problem codes.</summary>
internal sealed class DriversModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "drivers",
        Name = "Devices & drivers",
        Description = "Devices whose driver reports a problem code in Device Manager.",
        Reads = ["Plug-and-play device status (WMI)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(4),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var rows = Wmi.TryQuery(@"root\cimv2",
            "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0",
            out var error);
        if (error is not null)
        {
            notes.Add($"PnP query: {error}");
            return Task.FromResult(ModuleStatus.Failed);
        }

        var problems = new List<string>();
        var disabledCount = 0;
        foreach (var row in rows)
        {
            var code = Wmi.Get<uint>(row, "ConfigManagerErrorCode") ?? 0;
            if (code == 22)
            {
                disabledCount++; // disabled by the user — a choice, not a fault
                continue;
            }
            var name = Wmi.GetString(row, "Name") ?? Wmi.GetString(row, "DeviceID") ?? "Unknown device";
            problems.Add($"{name} (code {code})");
        }

        var detail = problems.Count == 0
            ? "No devices report driver problems."
            : "Problem devices: " + string.Join("; ", problems.Take(10)) + (problems.Count > 10 ? $" … and {problems.Count - 10} more" : "");

        metrics.Add(new Metric("drivers.problem_count", "Devices with driver problems", problems.Count,
            HealthCategory.Stability, Detail: detail));
        if (disabledCount > 0)
        {
            metrics.Add(new Metric("drivers.disabled_count", "Devices disabled by user", disabledCount,
                HealthCategory.Stability, InfoOnly: true));
        }

        return Task.FromResult(ModuleStatus.Completed);
    }
}
