using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>OS edition/build, hardware model, uptime, pending reboot, OS support status.</summary>
internal sealed class SysOverviewModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "sys.overview",
        Name = "System overview",
        Description = "Windows edition, build and support status (on Windows 10, whether Extended Security Updates are active), whether Windows 10 can move to Windows 11, hardware model, uptime, and whether a reboot is pending.",
        Reads = ["Windows version info, and the install date to compare with the Windows 11 verdict's date (WMI)", "Windows edition and the running build's update number (registry)", "On Windows 10, whether an Extended Security Updates licence is active and has a key installed (licensing WMI; the key itself is not kept)", "Memory in use and the limit Windows can hand out (WMI)", "Names, memory use and processor time of running programs (the top five are shown, by name only)", "Computer model (WMI)", "Pending-reboot registry flags", "Windows' own Windows 11 compatibility verdict (registry, AppCompatFlags)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(5),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        var os = Wmi.TryQuery(@"root\cimv2",
            "SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, InstallDate, TotalVisibleMemorySize, TotalVirtualMemorySize, FreeVirtualMemory FROM Win32_OperatingSystem",
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

            if (build == 0)
            {
                failures.Record("Windows build", "Win32_OperatingSystem gave no build number — whether this Windows is still supported was NOT checked.");
            }
            else
            {
                WindowsSupport.AddMetrics(metrics, failures, build, ReadEditionAndUbr, ReadEsuLicences, DateTime.UtcNow);
                // Only compared with the Windows 11 verdict's date; the report never gives it.
                var installedUtc = Wmi.GetCimDate(os, "InstallDate")?.ToUniversalTime();
                if (build >= Win11Readiness.FirstWindows11Build) AddWindows11HardwareSupport(metrics, failures, installedUtc);
                else if (build >= 10240) AddWindows11Readiness(metrics, failures, installedUtc);
            }
        }
        else
        {
            // Every graded verdict this module produces is derived from that one row — above all
            // os.supported, the warning that this Windows no longer receives security updates.
            // The pending-reboot metric further down is added unconditionally,
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

    private const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>
    /// EditionID ("Professional", "Core", "EnterpriseS") and UBR, the update number after the
    /// build's dot (19045.6466). Both are readable without admin. Missing either one throws:
    /// the support verdict is not decided without them.
    /// </summary>
    private static (string EditionId, int Ubr) ReadEditionAndUbr()
    {
        using var key = Registry.LocalMachine.OpenSubKey(VersionKey)
            ?? throw new InvalidDataException("the registry has no Windows version key");
        if (key.GetValue("EditionID") is not string { Length: > 0 } edition || key.GetValue("UBR") is not int ubr)
            throw new InvalidDataException("the registry holds no EditionID or UBR (the running build's update number)");
        return (edition.Trim(), ubr);
    }

    /// <summary>
    /// The ESU add-on licence rows. Only the ID, the licence state and whether a key is installed
    /// are kept; <see cref="WindowsSupport.Assess"/> picks Microsoft's published IDs out of them.
    /// </summary>
    private static IReadOnlyList<EsuLicence> ReadEsuLicences() =>
        [.. Wmi.Query(@"root\cimv2", "SELECT ID, LicenseStatus, PartialProductKey FROM SoftwareLicensingProduct WHERE LicenseIsAddon = TRUE")
            .Select(row => new EsuLicence(
                Guid.TryParse(Wmi.GetString(row, "ID"), out var id) ? id : Guid.Empty,
                Wmi.Get<uint>(row, "LicenseStatus") ?? 0,
                !string.IsNullOrEmpty(Wmi.GetString(row, "PartialProductKey"))))];

    private const string AppraiserKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\TargetVersionUpgradeExperienceIndicators";

    /// <summary>
    /// Windows' stored compatibility verdicts, or null when the registry could not be read (that
    /// is recorded as a failure, naming <paramref name="notChecked"/>).
    /// </summary>
    private static List<UpgradeAssessment>? ReadAssessments(ModuleFailures failures, string what, string notChecked)
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
            failures.Record(what, $"{e.Message} — {notChecked} was NOT checked.");
            return null;
        }
        return assessments;
    }

    /// <summary>
    /// What to do about "no verdict yet". The Microsoft Compatibility Appraiser task (Task Scheduler,
    /// \Microsoft\Windows\Application Experience) runs daily at 03:00 and, when the PC was off then,
    /// as soon as it can: read on the dev T470p, Windows 10 22H2, 2026-10-27.
    /// </summary>
    private const string AppraiserRunsDaily =
        "Windows runs that check by itself about once a day (its Microsoft Compatibility Appraiser task): leave the PC on " +
        "for a day, then scan again. If there is still no result, a company policy or a switched-off task may be stopping it.";

    /// <summary>Where a verdict came from, for its detail line. The date matters: a restored disk image brings its own.</summary>
    private static string SourceOf(UpgradeAssessment target)
    {
        var asOf = target.AssessedAt is { } at ? $", assessed {at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : "";
        return $"Windows' own compatibility check (the one Windows Update uses), for Windows 11 build {target.DestinationBuild}{asOf},";
    }

    /// <summary>
    /// The Note for a verdict assessed before this Windows was installed (<see cref="Win11Readiness.PredatesInstall"/>):
    /// neither a Good nor a Normal, and no blocker codes, since they may be another PC's.
    /// </summary>
    private static bool NotThisInstall(ModuleFailures failures, UpgradeAssessment target, DateTime? installedUtc, string notChecked)
    {
        if (!Win11Readiness.PredatesInstall(target, installedUtc, DateTime.UtcNow)) return false;
        failures.Note($"The Windows 11 verdict Windows keeps here is dated " +
                      $"{target.AssessedAt!.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, before this Windows " +
                      $"was installed or last upgraded, so it may be about an earlier Windows or the PC a disk image was made on, and {notChecked} was NOT established. " +
                      AppraiserRunsDaily);
        return true;
    }

    private static void AddBlockerCodes(List<Metric> metrics, Win11Verdict verdict) =>
        metrics.Add(new Metric("os.win11_blockers", "Windows 11 blockers (Windows' own codes)",
            verdict.BlockerCodes.Count == 0 ? "none" : string.Join(' ', verdict.BlockerCodes),
            HealthCategory.SoftwareOs, InfoOnly: true, Detail: $"{SourceOf(verdict.Assessment)} verdict {verdict.Assessment.UpgradeExperience}."));

    /// <summary>
    /// On Windows 11: whether Microsoft supports Windows 11 on this hardware, by the same verdict
    /// Windows keeps for the next Windows 11 version (decision 9: Windows 11 is restored onto
    /// T470 laptops whose processors Windows 11 does not support). On such hardware Microsoft says
    /// updates are "not guaranteed", security updates included
    /// (https://support.microsoft.com/en-us/windows/installing-windows-11-on-devices-that-don-t-meet-minimum-system-requirements-0b2dc4a2-5933-4ad4-9c09-ef0a331518f1),
    /// and a Red verdict is what keeps Windows Update from offering the next version. Without a
    /// verdict this app does not guess from the processor name: it says it does not know.
    /// </summary>
    private static void AddWindows11HardwareSupport(List<Metric> metrics, ModuleFailures failures, DateTime? installedUtc)
    {
        const string notChecked = "whether Microsoft supports Windows 11 on this hardware";
        if (ReadAssessments(failures, "Windows 11 hardware support", notChecked) is not { } assessments) return;

        if (Win11Readiness.Assess(assessments) is not { } verdict)
        {
            failures.Note($"Windows' own Windows 11 compatibility check has no result on this PC yet, so {notChecked} was NOT established. " +
                          AppraiserRunsDaily);
            return;
        }
        if (NotThisInstall(failures, verdict.Assessment, installedUtc, notChecked)) return;
        AddBlockerCodes(metrics, verdict);

        var source = SourceOf(verdict.Assessment);
        if (verdict.Ready == true)
        {
            metrics.Add(new Metric("os.win11_hardware_supported", "Hardware supported by Windows 11", true, HealthCategory.SoftwareOs,
                Detail: $"{source} found no blocker."));
        }
        else if (verdict.Ready == false && verdict.RequirementBlocked)
        {
            metrics.Add(new Metric("os.win11_hardware_supported", "Hardware supported by Windows 11", false, HealthCategory.SoftwareOs,
                Detail: "Windows 11 on hardware Microsoft does not support: updates are not guaranteed, and feature updates are not offered. " +
                        $"{source} says no: {string.Join("; ", verdict.Blockers)}."));
        }
        else
        {
            failures.Note($"Windows' verdict on this hardware is '{verdict.Assessment.UpgradeExperience}' " +
                          $"({(verdict.BlockerCodes.Count == 0 ? "no reason given" : string.Join(' ', verdict.BlockerCodes))}), " +
                          $"which this app does not interpret, so {notChecked} was NOT graded.");
        }
    }

    /// <summary>
    /// Whether this Windows 10 PC can move to Windows 11, by Windows' own verdict (see
    /// <see cref="Win11Readiness"/>). With Windows 10 out of support, this decides whether a PC has
    /// a future more often than any health reading does. Not assessed means NOT checked, and says so.
    /// </summary>
    private static void AddWindows11Readiness(List<Metric> metrics, ModuleFailures failures, DateTime? installedUtc)
    {
        const string notChecked = "whether this PC can move to Windows 11";
        if (ReadAssessments(failures, "Windows 11 readiness", notChecked) is not { } assessments) return;

        if (Win11Readiness.Assess(assessments) is not { } verdict)
        {
            failures.Note("Windows' own Windows 11 compatibility check has no result on this PC yet, so whether it can move to " +
                          "Windows 11 was NOT checked. " + AppraiserRunsDaily);
            return;
        }
        if (NotThisInstall(failures, verdict.Assessment, installedUtc, notChecked)) return;

        var target = verdict.Assessment;
        var source = SourceOf(target);
        AddBlockerCodes(metrics, verdict);

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
