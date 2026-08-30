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
        var failures = new ModuleFailures(notes);

        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl"))
        {
            // A key that would not open used to become mode -1, and the metric then stated as a
            // fact that this PC writes no crash dumps. "Nobody managed to look" and "it is switched
            // off" are the same red row to whoever reads the report, and only one of them is a
            // finding about the PC, so say nothing about the mode unless the value was really read.
            if (key?.GetValue("CrashDumpEnabled") is int mode)
            {
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
            else
            {
                failures.Record("Crash-dump setting (CrashControl registry key)",
                    key is null
                        ? "the key could not be opened, so whether Windows writes crash dumps was NOT checked"
                        : "the CrashDumpEnabled value is not there, so whether Windows writes crash dumps was NOT checked");
            }
        }

        var auto = Wmi.TryQuery(@"root\cimv2", "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem", out var err1)
            .FirstOrDefault();
        failures.RecordIf("Automatic page-file setting", err1);

        var pagefilePresent = auto is not null && Wmi.Get<bool>(auto, "AutomaticManagedPagefile") == true;
        string? err2 = null;
        if (!pagefilePresent)
        {
            pagefilePresent = Wmi.TryQuery(@"root\cimv2", "SELECT Name FROM Win32_PageFileSetting", out err2).Count > 0;
            failures.RecordIf("Page file list", err2);
        }

        // Finding a page file is a fact either query can establish on its own; NOT finding one is
        // only a fact when both of them actually ran. Windows managing the page file itself is
        // exactly the case where Win32_PageFileSetting returns nothing, so with the ComputerSystem
        // read broken an empty list proves nothing — and reporting "no page file configured" from
        // it marks a healthy PC as faulty on the strength of a query that never answered.
        if (pagefilePresent || (err1 is null && err2 is null))
        {
            metrics.Add(new Metric("pagefile.present", "Page file configured", pagefilePresent, HealthCategory.Stability,
                Detail: "A page file on a healthy volume is required for crash dumps and protects against out-of-memory failures."));
        }
        else
        {
            failures.Note("Whether a page file is configured could not be established, so it is not reported above.");
        }

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }
}
