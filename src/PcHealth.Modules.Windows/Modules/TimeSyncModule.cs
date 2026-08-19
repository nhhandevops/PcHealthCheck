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
        Description = "Whether the Windows Time service keeps the clock synchronized. A drifting clock breaks secure websites, updates, and activation.",
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
            var state = Wmi.GetString(service, "State") ?? "Unknown";
            var running = state.Equals("Running", StringComparison.OrdinalIgnoreCase);
            metrics.Add(new Metric("time.sync.service_running", "Time service running", running,
                HealthCategory.SoftwareOs,
                Detail: $"W32Time service: {state} (start mode {Wmi.GetString(service, "StartMode")}). " +
                        "Note: on many home PCs this service runs on demand, so 'stopped' alone is not alarming if the clock is correct."));
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
