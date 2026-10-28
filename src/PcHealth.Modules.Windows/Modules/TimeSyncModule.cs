using System.Text.RegularExpressions;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Clock/time-sync health — significant drift silently breaks TLS, Windows Update,
/// and license activation. Service state comes from WMI (locale-proof); w32tm output
/// is parsed best-effort only.
/// </summary>
internal sealed partial class TimeSyncModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "time.sync",
        Name = "Clock synchronization",
        Description = "The Windows Time service's state and time source, for information. A drifting clock breaks secure websites, updates, and activation.",
        Reads = ["Windows Time service state (WMI)", "w32tm status (read-only query)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(3),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var service = Wmi.TryQuery(@"root\cimv2",
            "SELECT State, StartMode FROM Win32_Service WHERE Name = 'W32Time'", out var error).FirstOrDefault();
        if (error is not null) notes.Add($"W32Time service query: {error}");

        if (service is not null)
        {
            // Information only. On a PC outside a domain W32Time is a trigger-start service, "Manual" and
            // "Stopped" by design, started by the weekly Time Synchronization task and then let stop
            // (https://learn.microsoft.com/en-us/troubleshoot/windows-client/active-directory/w32time-not-start-on-workgroup).
            // Grading "stopped" put a Normal row on every healthy workgroup PC, freshly imaged ones included.
            var state = Wmi.GetString(service, "State") ?? "Unknown";
            var running = state.Equals("Running", StringComparison.OrdinalIgnoreCase);
            metrics.Add(new Metric("time.sync.service_running", "Time service running", running,
                HealthCategory.SoftwareOs, InfoOnly: true,
                Detail: $"W32Time service: {state} (start mode {Wmi.GetString(service, "StartMode")}). " +
                        "On a PC that is not in a company domain, Windows starts this service only to synchronize the clock, " +
                        "about once a week, and lets it stop again, so 'stopped' is normal. Whether the clock itself is right " +
                        "is not checked yet. If it shows the wrong time, open Date & time in Settings " +
                        "(Win+R, then ms-settings:dateandtime) and choose Sync now."));
        }

        var status = await context.Processes.RunAsync(Manifest.Id, "w32tm", "/query /status",
            TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
        if (status.Succeeded)
        {
            // Best-effort: 'Stratum' appears untranslated on many locales; skip silently when it doesn't.
            var match = StratumRegex().Match(status.StdOut);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var stratum))
            {
                metrics.Add(new Metric("time.sync.stratum", "Time source stratum", (double)stratum,
                    HealthCategory.SoftwareOs, InfoOnly: true,
                    Detail: stratum is >= 1 and <= 15
                        ? "Clock is disciplined by a time source."
                        : "Clock is not currently synchronized to any source."));
            }
        }
        else
        {
            notes.Add("w32tm /query /status unavailable (service may be stopped).");
        }

        return metrics.Count > 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted;
    }

    [GeneratedRegex(@"Stratum\s*:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StratumRegex();
}
