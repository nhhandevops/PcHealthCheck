using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Linux.Support;

namespace PcHealth.Modules.Linux.Modules;

/// <summary>
/// Memory in use, memory promised to programs, swap, and out-of-memory kills since boot. The
/// counterpart of the Windows memory checks, with one difference that matters: Linux usually
/// promises more memory than it has (overcommit), so the commit figure is graded only on a
/// system set to strict accounting, the one setting where its limit is real.
/// </summary>
internal sealed class LinuxMemoryModule(SystemFiles files) : HealthModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "linux.memory",
        Name = "Memory",
        Description = "Memory in use, the memory promised to programs, swap, and whether the kernel has had to close programs for lack of memory since the last restart.",
        Reads =
        [
            "Memory and swap totals (/proc/meminfo)",
            "Overcommit setting (/proc/sys/vm/overcommit_memory)",
            "Out-of-memory kill counter (/proc/vmstat)",
        ],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(1),
    };

    private const double KbPerGb = 1048576.0;

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        var text = files.TryRead("/proc/meminfo", out var error);
        var info = text is null ? null : ProcFiles.ParseMeminfo(text);
        if (info is null || !info.TryGetValue("MemTotal", out var totalKb) || totalKb <= 0)
        {
            failures.Record("Memory totals (/proc/meminfo)", error ?? "no MemTotal line");
            return Task.FromResult(failures.Resolve(gotAnything: false));
        }

        metrics.Add(new Metric("ram.total_gb", "Usable RAM", Math.Round(totalKb / KbPerGb, 1), HealthCategory.Memory,
            Unit: "GB", InfoOnly: true, Detail: "What the kernel can use, slightly less than what is installed."));

        if (info.TryGetValue("MemAvailable", out var availableKb))
        {
            var usedKb = totalKb - Math.Min(availableKb, totalKb);
            metrics.Add(new Metric("memory.used_pct", "Memory in use", Math.Round(usedKb * 100.0 / totalKb, 1),
                HealthCategory.Memory, Unit: "%",
                Detail: $"{usedKb / KbPerGb:0.0} GB of {totalKb / KbPerGb:0.0} GB. Memory the kernel is only using as file " +
                        "cache counts as free, because it is handed back the moment a program needs it. Measured while the scan ran."));
        }
        else
        {
            failures.Record("Memory in use", "this kernel does not report MemAvailable (added in Linux 3.14)");
        }

        if (info.TryGetValue("SwapTotal", out var swapKb))
        {
            var swapFreeKb = info.GetValueOrDefault("SwapFree");
            // A value, not a detail: invariant like every other value, so "4.0 GB" reads the same in every report.
            metrics.Add(new Metric("memory.swap", "Swap",
                swapKb > 0
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{swapKb / KbPerGb:0.0} GB, {Math.Round((swapKb - Math.Min(swapFreeKb, swapKb)) * 100.0 / swapKb)}% in use")
                    : "none",
                HealthCategory.Memory, InfoOnly: true,
                Detail: "Space on disk (or compressed memory, zram) the kernel can move idle memory to. Without any, " +
                        "running out of RAM closes programs sooner."));
        }

        AddCommit(info, metrics, failures);
        AddOomKills(metrics, failures);
        return Task.FromResult(failures.Resolve());
    }

    private void AddCommit(Dictionary<string, long> info, List<Metric> metrics, ModuleFailures failures)
    {
        if (!info.TryGetValue("CommitLimit", out var limitKb) || limitKb <= 0 ||
            !info.TryGetValue("Committed_AS", out var committedKb))
            return;

        var pct = Math.Round(committedKb * 100.0 / limitKb);
        var amounts = $"{committedKb / KbPerGb:0.0} GB promised against a limit of {limitKb / KbPerGb:0.0} GB.";
        var mode = ProcFiles.ParseInt(files.TryRead("/proc/sys/vm/overcommit_memory", out var modeError));
        switch (mode)
        {
            case 2:
                metrics.Add(new Metric("memory.commit_pct", "Memory promised to programs", pct, HealthCategory.Memory, Unit: "%",
                    Detail: $"{amounts} This system uses strict overcommit (vm.overcommit_memory = 2), so at 100% programs are " +
                            "refused memory. Measured while the scan ran."));
                break;
            case 0 or 1:
                metrics.Add(new Metric("memory.commit_pct", "Memory promised to programs", pct, HealthCategory.Memory, Unit: "%",
                    InfoOnly: true,
                    Detail: $"{amounts} This system overcommits (vm.overcommit_memory = {mode}, the usual setting), so that " +
                            "limit is not enforced and going past it is normal. What runs out is RAM plus swap: see " +
                            "'Memory in use' and the out-of-memory count."));
                break;
            default:
                failures.Record("Overcommit setting", modeError ?? "/proc/sys/vm/overcommit_memory did not hold 0, 1 or 2");
                metrics.Add(new Metric("memory.commit_pct", "Memory promised to programs", pct, HealthCategory.Memory, Unit: "%",
                    InfoOnly: true,
                    Detail: $"{amounts} Not graded: whether this limit is enforced could not be read."));
                break;
        }
    }

    private void AddOomKills(List<Metric> metrics, ModuleFailures failures)
    {
        var vmstat = files.TryRead("/proc/vmstat", out var error);
        if (vmstat is null)
        {
            failures.Record("Out-of-memory kills", error ?? "unreadable");
            return;
        }
        if (ProcFiles.VmStatCounter(vmstat, "oom_kill") is not { } kills)
        {
            failures.Note("This kernel does not count out-of-memory kills (no oom_kill in /proc/vmstat; added in Linux 4.13), so they were NOT checked.");
            return;
        }
        metrics.Add(new Metric("memory.oom_kills_since_boot", "Programs closed for lack of memory", (double)kills, HealthCategory.Memory,
            Detail: "Times the kernel closed a program to free memory since the last restart. systemd-oomd, which Ubuntu " +
                    "and Fedora also run, steps in earlier and is not counted here; its kills are listed by " +
                    "'journalctl -u systemd-oomd'."));
    }
}
