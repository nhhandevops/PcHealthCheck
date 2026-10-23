using System.Runtime.InteropServices;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Linux.Support;

namespace PcHealth.Modules.Linux.Modules;

/// <summary>Distribution, kernel, computer model and time since the last restart.</summary>
internal sealed class LinuxOverviewModule(SystemFiles files) : HealthModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "linux.overview",
        Name = "System overview",
        Description = "Distribution and kernel version, computer model, and time since the last restart.",
        Reads =
        [
            "Distribution name and version (/etc/os-release)",
            "Kernel version (/proc/sys/kernel/osrelease)",
            "Computer maker and model (/sys/class/dmi/id: sys_vendor, product_name, product_version; never the serial number)",
            "Time since boot (/proc/uptime)",
        ],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(1),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        var osRelease = files.TryRead("/etc/os-release", out var osError) ?? files.TryRead("/usr/lib/os-release");
        if (osRelease is null) failures.Record("Distribution name", osError ?? "no os-release file");
        var kernel = files.TryRead("/proc/sys/kernel/osrelease", out var kernelError);
        if (kernel is null) failures.Record("Kernel version", kernelError ?? "unreadable");
        metrics.Add(new Metric("os.build", "Operating system",
            ProcFiles.DescribeOs(osRelease is null ? null : ProcFiles.ParseOsRelease(osRelease), kernel,
                RuntimeInformation.OSArchitecture.ToString()),
            HealthCategory.SoftwareOs, InfoOnly: true));

        var uptimeText = files.TryRead("/proc/uptime", out var uptimeError);
        if (uptimeText is not null && ProcFiles.ParseUptimeSeconds(uptimeText) is { } seconds)
        {
            var boot = DateTime.Now - TimeSpan.FromSeconds(seconds);
            metrics.Add(new Metric("os.uptime_days", "Time since last restart", Math.Round(seconds / 86400, 1),
                HealthCategory.SoftwareOs, Unit: "days",
                Detail: $"Last boot {boot:yyyy-MM-dd HH:mm}. Restarting occasionally lets kernel and library updates take effect."));
        }
        else
        {
            failures.Record("Time since last restart", uptimeError ?? "/proc/uptime did not hold a number");
        }

        var model = ProcFiles.DescribeModel(
            files.TryRead("/sys/class/dmi/id/sys_vendor"),
            files.TryRead("/sys/class/dmi/id/product_name"),
            files.TryRead("/sys/class/dmi/id/product_version"));
        if (model is not null)
            metrics.Add(new Metric("hw.model", "Computer", model, HealthCategory.SoftwareOs, InfoOnly: true));
        else
            failures.Note("This machine's firmware does not publish its maker and model (no usable /sys/class/dmi/id), which is common on ARM boards.");

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }
}
