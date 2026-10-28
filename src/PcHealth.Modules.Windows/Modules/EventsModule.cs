using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Error mining from the System/Application event logs. Queries are keyed on
/// provider + event id (locale-independent) — never on localized message text.
/// An empty result means healthy, not a failed query, but only for as many days as the log goes
/// back: each count states its real span, and a zero from too short a log is not judged (<see cref="RecordSpan"/>).
/// </summary>
internal sealed class EventsModule : WindowsModuleBase
{
    private const int NinetyDays = 90;
    private const int ThirtyDays = 30;
    private const int SevenDays = 7;
    private const long DayMs = 24L * 60 * 60 * 1000;

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "events.errors",
        Name = "Error history (event logs)",
        Description = "Disk errors (and which drive; a USB drive's are shown but not counted against this PC), hardware (WHEA) errors, unexpected power loss, blue screens, GPU driver resets, low-memory warnings, firmware speed limits on the processor, and app-crash frequency from the Windows event logs.",
        Reads = ["System event log", "Application event log", "How far back each of the two logs goes (the date of its oldest record)", "Which disks are attached now, and their connection (WMI)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(8),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        // How far back each log goes, so a count over 90 days from 9 days of log says so (M1).
        var now = DateTime.UtcNow;
        var system = RecordSpan.ReadEventLog("System", now, out var systemError);
        failures.RecordIf("How far back the System event log goes", systemError);
        var application = RecordSpan.ReadEventLog("Application", now, out var applicationError);
        failures.RecordIf("How far back the Application event log goes", applicationError);

        AddDiskErrors(metrics, failures, system);
        ct.ThrowIfCancellationRequested();

        // Event 2004: Windows was close to running out of commit (RAM plus page file). Physical
        // memory in use cannot show this - on 2026-10-21 a laptop read "83% in use, Good" between
        // three of these, one of which took the desktop window manager down with it (HW-001).
        AddCount(metrics, failures, "System", system, ThirtyDays,
            "Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector']", "EventID=2004",
            "memory.low_memory_events_30d", "Low-memory warnings (30 days)", HealthCategory.Memory,
            "Times Windows logged that it was close to running out of memory (RAM plus page file, event 2004). " +
            "Each one usually means programs slowed down, refused to open, or crashed.");
        ct.ThrowIfCancellationRequested();

        // Event 37: firmware capped the processor's speed. Shown, not graded: laptops commonly log
        // a few at start-up while nothing is wrong, and no healthy-machine baseline exists yet to
        // set a threshold against. "Processor speed while busy" (CPU benchmark) is the graded measure.
        AddCount(metrics, failures, "System", system, ThirtyDays,
            "Provider[@Name='Microsoft-Windows-Kernel-Processor-Power']", "EventID=37",
            "events.cpu_speed_limited_30d", "Processor speed limited by firmware (30 days)", HealthCategory.Performance,
            "Windows event 37: the firmware held the processor below its normal speed, usually for heat or power. " +
            "A few at start-up are common on laptops; many, spread through the day, suggest the cooling or the charger.",
            infoOnly: true);
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System", system, NinetyDays,
            "Provider[@Name='Microsoft-Windows-WHEA-Logger']", "(EventID=17 or EventID=19)",
            "events.whea_corrected_90d", "Corrected hardware errors (90 days)", HealthCategory.Stability,
            "Hardware errors the platform corrected (often PCIe or CPU cache). Rare ones are normal; daily ones are not.");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System", system, NinetyDays,
            "Provider[@Name='Microsoft-Windows-WHEA-Logger']", "(EventID=1 or EventID=18 or EventID=20)",
            "events.whea_uncorrectable_90d", "Uncorrectable hardware errors (90 days)", HealthCategory.Stability,
            "Fatal machine-check events — failing CPU, RAM, or motherboard territory.");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System", system, NinetyDays,
            "Provider[@Name='Microsoft-Windows-Kernel-Power']", "EventID=41",
            "events.kernel_power_41_90d", "Unexpected shutdowns (90 days)", HealthCategory.Stability,
            "Kernel-Power 41: the machine lost power or hard-froze without a clean shutdown.");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System", system, NinetyDays,
            "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting']", "EventID=1001",
            "events.bugcheck_90d", "Blue screens (90 days)", HealthCategory.Stability,
            "Recorded bugchecks (BSOD).");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "System", system, NinetyDays,
            "Provider[@Name='Display']", "EventID=4101",
            "events.gpu_tdr_90d", "GPU driver resets (90 days)", HealthCategory.Stability,
            "Display driver stopped responding and was recovered (TDR) — the classic dying-GPU or unstable-driver signal.");
        ct.ThrowIfCancellationRequested();

        AddCount(metrics, failures, "Application", application, SevenDays,
            "Provider[@Name='Application Error']", "EventID=1000",
            "events.app_crashes_7d", "Application crashes (7 days)", HealthCategory.Stability,
            "Crashes across all applications this week. Many crashes in many different apps can indicate RAM or disk trouble.",
            goodUpTo: 2); // rule.stab.app-crashes is Good up to 2, so 2 crashes from one day of log is not a clean week

        // One Note per log naming the zeros it was too short to judge. Not a failure: nothing broke.
        foreach (var span in new[] { system, application })
        {
            if (span.Note() is { } note) failures.Note(note);
        }

        // Six of these seven counts landing is not a clean scan: the seventh is a whole class of
        // fault — blue screens, disk errors — that nobody looked at this time. Counting on
        // metrics.Count let one surviving query hide every query that broke, so the omission
        // AddCount is careful to make was never mentioned anywhere the reader could see it.
        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    private static string Filter(int windowDays, string providerClause, string idClause) =>
        string.Create(CultureInfo.InvariantCulture,
            $"*[System[{providerClause} and {idClause} and TimeCreated[timediff(@SystemTime) <= {windowDays * DayMs}]]]");

    /// <summary>Counts one kind of event over the last <paramref name="windowDays"/> days, stating the log's real span.</summary>
    private static void AddCount(
        List<Metric> metrics, ModuleFailures failures, string log, RecordSpan span, int windowDays,
        string providerClause, string idClause,
        string metricId, string label, HealthCategory category, string explanation, bool infoOnly = false,
        double goodUpTo = 0)
    {
        int? count = CountEvents(log, Filter(windowDays, providerClause, idClause), failures, label);
        if (count is null) return; // query failed — failure recorded, metric omitted (Unknown, not zero)
        metrics.Add(span.Apply(
            new Metric(metricId, label, (double)count.Value, category, Detail: explanation, InfoOnly: infoOnly), windowDays, goodUpTo));
    }

    private static void AddDiskErrors(List<Metric> metrics, ModuleFailures failures, RecordSpan system)
    {
        const string label = "Disk I/O errors (90 days)";
        var devices = DiskErrorLog.Read(out var error);
        if (devices is null)
        {
            // Unknown, not zero: the metric is omitted and the failure named by what went uncounted.
            failures.Record($"'{label}' could not be counted from the System event log", error ?? "unknown error");
            return;
        }

        // Graded on this PC's own drives only. The PC was graded on every drive Windows had ever
        // seen, so on the probe PC errors logged by two USB drives read as its own disk failing,
        // and the kit had to undo it (KIT-005, REMEDIES-PLAN H3). The disk Windows runs from is
        // the one marked as the boot disk; unknown is fine, the attribution then names none.
        // When the attached disks cannot be listed, every error counts, as before, with a note.
        List<AttachedDisk>? attached = null;
        if (devices.Count > 0)
        {
            attached = DriveMap.ReadDisks(out var disksError);
            if (attached is null)
            {
                failures.Note($"Which drives are plugged in over USB could not be read ({disksError}), so every disk " +
                              "error is counted against this PC.");
            }
        }

        var share = DiskEventAttribution.Share(devices, attached);
        var byDrive = DiskEventAttribution.Describe(devices, attached?.FirstOrDefault(d => d.IsBoot)?.Number);
        var notCounted = DiskEventAttribution.DescribeShare(share);
        // The span goes last: the audit kit reads the split above from this Detail by its wording.
        metrics.Add(system.Apply(new Metric("events.disk_errors_90d", label, (double)share.AgainstThisPc, HealthCategory.Storage,
            Detail: "Bad blocks, retried I/O, controller resets, or NTFS corruption events. Any growth here deserves attention — and a backup." +
                    (notCounted.Length > 0 ? " " + notCounted : "") +
                    (byDrive.Length > 0 ? " " + byDrive : "")), NinetyDays));
    }

    private static int? CountEvents(
        string log, string xpath, ModuleFailures failures, string label, Action<EventRecord>? inspect = null)
    {
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var count = 0;
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    count++;
                    inspect?.Invoke(record);
                }
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
