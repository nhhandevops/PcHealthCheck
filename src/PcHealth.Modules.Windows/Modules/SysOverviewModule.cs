using System.Diagnostics;
using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>OS edition/build, hardware model, uptime, pending reboot, OS support status.</summary>
internal sealed class SysOverviewModule : WindowsModuleBase
{
    // Consumer ESU for Windows 10 ends 2027-10-12; after that any Win10 machine is out of support
    // regardless of enrollment. Microsoft moved this once already: it was 2026-10-13 until a quiet
    // documentation update on 2026-06-25, and the app kept telling every Windows 10 user they
    // had no updates left (HW-001). Re-check https://www.microsoft.com/en-us/windows/extended-security-updates
    // before trusting this date.
    private static readonly DateTime Windows10EndOfSupport = new(2027, 10, 12);

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "sys.overview",
        Name = "System overview",
        Description = "Windows edition, build and support status, whether Windows 10 can move to Windows 11, hardware model, uptime, and whether a reboot is pending.",
        Reads = ["Windows version info (WMI)", "Memory in use and the limit Windows can hand out (WMI)", "Names, memory use and processor time of running programs (the top five are shown, by name only)", "Computer model (WMI)", "Pending-reboot registry flags", "Windows' own Windows 11 compatibility verdict (registry, AppCompatFlags)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(3),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        var os = Wmi.TryQuery(@"root\cimv2",
            "SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, TotalVisibleMemorySize, TotalVirtualMemorySize, FreeVirtualMemory FROM Win32_OperatingSystem",
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
                    HealthCategory.Memory, Unit: "GB", InfoOnly: true));
            }

            // Commit, not physical use, is what runs out: Windows refuses or crashes programs when
            // the memory it has promised reaches RAM plus page file. TotalVirtualMemorySize is that
            // limit and FreeVirtualMemory what is left of it, both in KB.
            if (Wmi.Get<ulong>(os, "TotalVirtualMemorySize") is { } limitKb && limitKb > 0 &&
                Wmi.Get<ulong>(os, "FreeVirtualMemory") is { } freeKb)
            {
                var usedKb = limitKb - Math.Min(freeKb, limitKb);
                metrics.Add(new Metric("memory.commit_pct", "Memory promised to programs", Math.Round(100.0 * usedKb / limitKb),
                    HealthCategory.Memory, Unit: "%",
                    Detail: $"{usedKb / 1048576.0:0.0} GB of the {limitKb / 1048576.0:0.0} GB Windows can hand out (RAM plus page file). " +
                            "At 100% programs crash or refuse to start. Measured while the scan ran, so it reflects what was open then."));
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
                AddWindows11Readiness(metrics, failures);
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

        AddTopUsers(metrics);

        metrics.Add(new Metric("os.pending_reboot", "Reboot pending", IsRebootPending(), HealthCategory.SoftwareOs,
            Detail: "Checked Component Based Servicing and Windows Update reboot flags."));

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    private const string AppraiserKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\TargetVersionUpgradeExperienceIndicators";

    /// <summary>
    /// Whether this Windows 10 PC can move to Windows 11, by Windows' own verdict (see
    /// <see cref="Win11Readiness"/>). With Windows 10 near its end, this decides whether a PC has
    /// a future more often than any health reading does. Not assessed means NOT checked, and says so.
    /// </summary>
    private static void AddWindows11Readiness(List<Metric> metrics, ModuleFailures failures)
    {
        var assessments = new List<UpgradeAssessment>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(AppraiserKey);
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(name);
                if (key is null) continue;
                var values = key.GetValueNames().ToDictionary(
                    v => v, v => Win11Readiness.RegistryText(key.GetValue(v)), StringComparer.OrdinalIgnoreCase);
                if (Win11Readiness.FromValues(name, values) is { } assessment) assessments.Add(assessment);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            failures.Record("Windows 11 readiness", $"{e.Message} — whether this PC can move to Windows 11 was NOT checked.");
            return;
        }

        if (Win11Readiness.Assess(assessments) is not { } verdict)
        {
            failures.Note("Windows has not assessed this PC for Windows 11 (its compatibility check has not run here, or " +
                          "company policy turns it off), so whether it can move to Windows 11 was NOT checked.");
            return;
        }

        var target = verdict.Assessment;
        var asOf = target.AssessedAt is { } at ? $", assessed {at.ToLocalTime():yyyy-MM-dd}" : "";
        var source = $"Windows' own compatibility check (the one Windows Update uses), for Windows 11 build {target.DestinationBuild}{asOf},";
        metrics.Add(new Metric("os.win11_blockers", "Windows 11 blockers (Windows' own codes)",
            verdict.BlockerCodes.Count == 0 ? "none" : string.Join(' ', verdict.BlockerCodes),
            HealthCategory.SoftwareOs, InfoOnly: true, Detail: $"{source} verdict {target.UpgradeExperience}."));

        if (verdict.Ready is not { } ready)
        {
            failures.Note($"Windows' Windows 11 verdict here is '{target.UpgradeExperience}', which this app does not " +
                          "interpret, so whether this PC can move to Windows 11 was NOT graded.");
            return;
        }
        var hold = target.GatedBlockReason is { } reason
            ? $" Windows Update is holding the upgrade back for now (safeguard hold: {reason}); that lifts on its own once fixed."
            : "";
        metrics.Add(new Metric("os.win11_ready", "Can move to Windows 11", ready, HealthCategory.SoftwareOs,
            Detail: ready
                ? $"{source} found no blocker.{hold}"
                : $"{source} says no: {string.Join("; ", verdict.Blockers)}. " +
                  (verdict.ProcessorBlocked
                      ? "A processor block cannot be fixed by any setting."
                      : "TPM and Secure Boot can often be switched on in the firmware (BIOS/UEFI) settings; Windows re-checks on its own later.")));
    }

    /// <summary>
    /// Who holds the memory and who has used the processor, so a Memory or Performance finding
    /// names something to act on. Processor time is the total since each program started, not a
    /// sample taken now: a sample during the scan would mostly measure the scan itself.
    /// </summary>
    private static void AddTopUsers(List<Metric> metrics)
    {
        var memory = new List<(string, long)>();
        var cpu = new List<(string, TimeSpan)>();
        var cpuUnreadable = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == 0) continue; // the Idle pseudo-process
                string name;
                try { name = process.ProcessName; } catch (InvalidOperationException) { continue; } // exited meanwhile

                try { memory.Add((name, process.PrivateMemorySize64)); }
                catch (InvalidOperationException) { }

                // Protected system processes, and other accounts' processes without admin, refuse this.
                try { cpu.Add((name, process.TotalProcessorTime)); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { cpuUnreadable++; }
            }
        }

        var byMemory = ProcessSummary.TopByMemory(memory);
        if (byMemory.Length > 0)
        {
            metrics.Add(new Metric("memory.top_users", "Programs holding the most memory", byMemory,
                HealthCategory.Memory, InfoOnly: true,
                Detail: "Private memory, the memory each program has asked Windows to hold for it, summed per program " +
                        "(×N is how many processes it runs). Names only: no paths, window titles or command lines."));
        }

        var byCpu = ProcessSummary.TopByCpu(cpu);
        if (byCpu.Length > 0)
        {
            metrics.Add(new Metric("cpu.top_users", "Programs that used the most processor time", byCpu,
                HealthCategory.Performance, InfoOnly: true,
                Detail: "Processor time since each program started, summed per program; one hour means one core kept busy " +
                        "for an hour. Names only." +
                        (cpuUnreadable > 0
                            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                                $" {cpuUnreadable} protected or other-account processes could not be read and are not included.")
                            : "")));
        }
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
