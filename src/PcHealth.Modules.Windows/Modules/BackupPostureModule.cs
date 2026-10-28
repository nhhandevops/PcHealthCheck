using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Backup posture, the part that needs administrator rights: System Restore points and shadow
/// copies, which a standard account cannot list. Every failing-disk verdict in this app begins
/// with "back up first", so the app has to be able to answer the obvious follow-up: is anything
/// actually backing this PC up? File History and OneDrive belong to an account, so they are read
/// by <see cref="BackupFilesModule"/> in the normal app, which also draws that conclusion from this
/// check's shadow-copy count (REMEDIES-PLAN Step A row 7). Reads configuration only, never the
/// contents of backups.
/// </summary>
internal sealed class BackupPostureModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "backup.posture",
        Name = "Restore points & shadow copies",
        Description = "System Restore points and shadow copies (previous versions of files). Only an administrator can list them; File History and OneDrive are in the File backup check.",
        Reads = ["System Restore points (WMI)", "Shadow copies (WMI)"],
        NeedsAdmin = true,
        EstimatedDuration = TimeSpan.FromSeconds(6),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        // System Restore points (system settings/drivers — not personal files).
        var restorePoints = Wmi.TryQuery(@"root\default",
            "SELECT CreationTime, Description FROM SystemRestore", out var restoreError);
        if (restoreError is not null)
        {
            // A count is an assertion. A query that failed leaves the list empty, and emitting the
            // metric anyway would grade this PC on "no restore points exist" — a statement about
            // the reader's machine that this scan never established.
            failures.Record("System Restore query", restoreError);
        }
        else
        {
            DateTime? newestRestore = null;
            foreach (var point in restorePoints)
            {
                var created = Wmi.GetCimDate(point, "CreationTime");
                if (created is not null && (newestRestore is null || created > newestRestore)) newestRestore = created;
            }

            metrics.Add(new Metric("backup.restore_points", "System restore points", restorePoints.Count,
                HealthCategory.Storage,
                Detail: newestRestore is { } newest
                    ? $"Most recent: {newest:yyyy-MM-dd}. Restore points roll back Windows settings and drivers — they do NOT back up your documents."
                    : "No restore points exist. System Protection may be turned off (Control Panel > System > System Protection)."));

            if (newestRestore is { } latest)
            {
                metrics.Add(new Metric("backup.restore_point_age_days", "Newest restore point age",
                    Math.Round((DateTime.Now - latest).TotalDays, 0), HealthCategory.Storage, Unit: "days"));
            }
        }

        // Volume Shadow Copies (previous versions of files). The file-backup check finds this count
        // among the scan's findings by its id.
        var shadows = Wmi.TryQuery(@"root\cimv2", "SELECT ID, InstallDate FROM Win32_ShadowCopy", out var shadowError);
        if (shadowError is not null)
        {
            // Same reason as the restore points: zero shadow copies read from a broken query would
            // tell someone "'Previous versions' recovery is not available" without having looked.
            failures.Record("Shadow copy query", shadowError);
        }
        else
        {
            metrics.Add(new Metric(BackupProtection.ShadowCopiesMetricId, "Shadow copies (previous versions)", shadows.Count,
                HealthCategory.Storage, InfoOnly: true,
                Detail: shadows.Count > 0
                    ? "Previous versions of files are available for recovery on this PC."
                    : "No shadow copies — 'Previous versions' recovery is not available."));
        }

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }
}
