using System.Diagnostics.Eventing.Reader;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Error mining from the System/Application event logs. Queries are keyed on
/// provider + event id (locale-independent) — never on localized message text.
/// An empty result means healthy, not a failed query.
/// </summary>
internal sealed class EventsModule : WindowsModuleBase
{
    private const long NinetyDaysMs = 90L * 24 * 60 * 60 * 1000;
    private const long SevenDaysMs = 7L * 24 * 60 * 60 * 1000;

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "events.errors",
        Name = "Error history (event logs)",
        Description = "Disk errors, hardware (WHEA) errors, unexpected power loss, blue screens, GPU driver resets, and app-crash frequency from the Windows event logs.",
        Reads = ["System event log", "Application event log"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(8),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        AddCount(metrics, failures, "System",
            Filter(NinetyDaysMs,
                "(Provider[@Name='disk'] or Provider[@Name='stordisk'] or Provider[@Name='storahci'] or Provider[@Name='stornvme'] or Provider[@Name='Ntfs'])",
                "(EventID=7 or EventID=51 or EventID=55 or EventID=129 or EventID=153 or EventID=157)"),
            "events.disk_errors_90d", "Disk I/O errors (90 days)", HealthCategory.Storage,
            "Bad blocks, retried I/O, controller resets, or NTFS corruption events. Any growth here deserves attention — and a backup.");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System",
            Filter(NinetyDaysMs, "Provider[@Name='Microsoft-Windows-WHEA-Logger']", "(EventID=17 or EventID=19)"),
            "events.whea_corrected_90d", "Corrected hardware errors (90 days)", HealthCategory.Stability,
            "Hardware errors the platform corrected (often PCIe or CPU cache). Rare ones are normal; daily ones are not.");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System",
            Filter(NinetyDaysMs, "Provider[@Name='Microsoft-Windows-WHEA-Logger']", "(EventID=1 or EventID=18 or EventID=20)"),
            "events.whea_uncorrectable_90d", "Uncorrectable hardware errors (90 days)", HealthCategory.Stability,
            "Fatal machine-check events — failing CPU, RAM, or motherboard territory.");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System",
            Filter(NinetyDaysMs, "Provider[@Name='Microsoft-Windows-Kernel-Power']", "EventID=41"),
            "events.kernel_power_41_90d", "Unexpected shutdowns (90 days)", HealthCategory.Stability,
            "Kernel-Power 41: the machine lost power or hard-froze without a clean shutdown.");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System",
            Filter(NinetyDaysMs, "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting']", "EventID=1001"),
            "events.bugcheck_90d", "Blue screens (90 days)", HealthCategory.Stability,
            "Recorded bugchecks (BSOD).");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System",
            Filter(NinetyDaysMs, "Provider[@Name='Display']", "EventID=4101"),
            "events.gpu_tdr_90d", "GPU driver resets (90 days)", HealthCategory.Stability,
            "Display driver stopped responding and was recovered (TDR) — the classic dying-GPU or unstable-driver signal.");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "Application",
            Filter(SevenDaysMs, "Provider[@Name='Application Error']", "EventID=1000"),
            "events.app_crashes_7d", "Application crashes (7 days)", HealthCategory.Stability,
            "Crashes across all applications this week. Many crashes in many different apps can indicate RAM or disk trouble.");

        // Six of these seven counts landing is not a clean scan: the seventh is a whole class of
        // fault — blue screens, disk errors — that nobody looked at this time. Counting on
        // metrics.Count let one surviving query hide every query that broke, so the omission
        // AddCount is careful to make was never mentioned anywhere the reader could see it.
        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    private static string Filter(long windowMs, string providerClause, string idClause) =>
        $"*[System[{providerClause} and {idClause} and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]";

    private static void AddCount(
        List<Metric> metrics, ModuleFailures failures, string log, string xpath,
        string metricId, string label, HealthCategory category, string explanation)
    {
        int? count = CountEvents(log, xpath, failures, label);
        if (count is null) return; // query failed — failure recorded, metric omitted (Unknown, not zero)
        metrics.Add(new Metric(metricId, label, (double)count.Value, category, Detail: explanation));
    }

    private static int? CountEvents(string log, string xpath, ModuleFailures failures, string label)
    {
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var count = 0;
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record) { count++; }
                if (count >= 5000) break; // cap pathological logs; 5000 already means "very bad"
            }
            return count;
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException)
        {
            // Named by the count that is missing rather than by the log: "the System log failed"
            // does not tell the reader that blue screens are the thing nobody counted this scan.
            failures.Record($"'{label}' could not be counted from the {log} event log", e.Message);
            return null;
        }
    }
}
