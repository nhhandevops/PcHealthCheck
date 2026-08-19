using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>Crash-dump configuration — BSOD analysis silently assumes dumps exist, so verify they can be written.</summary>
internal sealed class CrashDumpModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "crashdump.config",
        Name = "Crash dump settings",
        Description = "Whether Windows is configured to write crash dumps (needed to diagnose blue screens) and a page file exists.",
        Reads = ["CrashControl registry key", "Page file configuration (WMI)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(2),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl"))
        {
            var mode = key?.GetValue("CrashDumpEnabled") as int? ?? -1;
            var modeName = mode switch
            {
                0 => "none",
                1 => "complete memory dump",
                2 => "kernel memory dump",
                3 => "small memory dump (minidump)",
                7 => "automatic memory dump",
                _ => $"unknown ({mode})",
            };
            metrics.Add(new Metric("crashdump.enabled", "Crash dumps enabled", mode > 0, HealthCategory.Stability,
                Detail: $"Configured mode: {modeName}. Without dumps, blue-screen causes cannot be diagnosed afterwards."));
        }

        var auto = Wmi.TryQuery(@"root\cimv2", "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem", out var err1)
            .FirstOrDefault();
        var pagefilePresent = auto is not null && Wmi.Get<bool>(auto, "AutomaticManagedPagefile") == true;
        if (!pagefilePresent)
        {
            pagefilePresent = Wmi.TryQuery(@"root\cimv2", "SELECT Name FROM Win32_PageFileSetting", out var err2).Count > 0;
            if (err2 is not null) notes.Add($"PageFileSetting query: {err2}");
        }
        if (err1 is not null) notes.Add($"ComputerSystem query: {err1}");

        metrics.Add(new Metric("pagefile.present", "Page file configured", pagefilePresent, HealthCategory.Stability,
            Detail: "A page file on a healthy volume is required for crash dumps and protects against out-of-memory failures."));

        return Task.FromResult(ModuleStatus.Completed);
    }
}
