using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>OS edition/build, hardware model, uptime, pending reboot, OS support status.</summary>
internal sealed class SysOverviewModule : WindowsModuleBase
{
    // Consumer ESU for Windows 10 ends 2026-10-13; after that any Win10 machine is out of support
    // regardless of enrollment (research critic correction).
    private static readonly DateTime Windows10EndOfSupport = new(2026, 10, 13);

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "sys.overview",
        Name = "System overview",
        Description = "Windows edition, build and support status, hardware model, uptime, and whether a reboot is pending.",
        Reads = ["Windows version info (WMI)", "Computer model (WMI)", "Pending-reboot registry flags"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(3),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        var os = Wmi.TryQuery(@"root\cimv2",
            "SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, TotalVisibleMemorySize FROM Win32_OperatingSystem",
            out var osError).FirstOrDefault();

        if (os is not null)
        {
            var caption = Wmi.GetString(os, "Caption")?.Trim() ?? "Windows";
            var build = int.TryParse(Wmi.GetString(os, "BuildNumber"), out var b) ? b : 0;
            metrics.Add(new Metric("os.build", "Operating system", $"{caption} (build {build}, {Wmi.GetString(os, "OSArchitecture")})",
                HealthCategory.SoftwareOs, InfoOnly: true));

            if (Wmi.GetCimDate(os, "LastBootUpTime") is { } boot)
            {
                var uptimeDays = Math.Round((DateTime.Now - boot).TotalDays, 1);
                metrics.Add(new Metric("os.uptime_days", "Time since last restart", uptimeDays,
                    HealthCategory.SoftwareOs, Unit: "days",
                    Detail: $"Last boot {boot:yyyy-MM-dd HH:mm}. Restarting occasionally lets updates finish and clears degraded state."));
            }

            if (Wmi.Get<ulong>(os, "TotalVisibleMemorySize") is { } ramKb)
            {
                metrics.Add(new Metric("ram.total_gb", "Installed RAM", Math.Round(ramKb / 1048576.0, 1),
                    HealthCategory.SoftwareOs, Unit: "GB", InfoOnly: true));
            }

            var isWindows11 = build >= 22000;
            var isWindows10 = build is >= 10240 and < 22000;
            if (isWindows10)
            {
                var supported = DateTime.Now < Windows10EndOfSupport;
                metrics.Add(new Metric("os.supported", "OS support status", supported, HealthCategory.SoftwareOs,
                    Detail: supported
                        ? $"Windows 10 security updates (incl. consumer ESU) end {Windows10EndOfSupport:yyyy-MM-dd}."
                        : "Windows 10 is out of support — no more security updates, even with ESU."));
                if (supported)
                {
                    metrics.Add(new Metric("os.win10_eol_near", "Windows 10 end of support approaching", true,
                        HealthCategory.SoftwareOs,
                        Detail: $"All Windows 10 updates stop on {Windows10EndOfSupport:yyyy-MM-dd}."));
                }
            }
            else
            {
                metrics.Add(new Metric("os.supported", "OS support status", isWindows11, HealthCategory.SoftwareOs,
                    Detail: isWindows11 ? "Windows 11 — in support." : "Unrecognized/very old Windows build."));
            }
        }
        else
        {
            // Every graded verdict this module produces is derived from that one row — above all
            // os.supported, the warning that a Windows 10 machine is about to stop receiving
            // security updates. The pending-reboot metric further down is added unconditionally,
            // so without degrading here the module always finishes holding at least one metric,
            // reports Completed, and the end-of-support verdict disappears with nobody told.
            failures.Record("Windows version",
                $"{osError ?? "Win32_OperatingSystem returned no rows"} — the Windows edition, uptime and " +
                "whether this Windows is still supported were NOT checked.");
        }

        var cs = Wmi.TryQuery(@"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem",
            failures, "Computer model query").FirstOrDefault();
        if (cs is not null)
        {
            metrics.Add(new Metric("hw.model", "Computer", $"{Wmi.GetString(cs, "Manufacturer")} {Wmi.GetString(cs, "Model")}".Trim(),
                HealthCategory.SoftwareOs, InfoOnly: true));
        }

        metrics.Add(new Metric("os.pending_reboot", "Reboot pending", IsRebootPending(), HealthCategory.SoftwareOs,
            Detail: "Checked Component Based Servicing and Windows Update reboot flags."));

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    private static bool IsRebootPending()
    {
        static bool KeyExists(string path)
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }

        return KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
            || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
    }
}
