using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Backup posture. Every failing-disk verdict in this app begins with "back up first",
/// so the app has to be able to answer the obvious follow-up: is anything actually
/// backing this PC up? Checks System Restore points, shadow copies, File History, and
/// OneDrive folder backup. Reads configuration only — never the contents of backups.
/// </summary>
internal sealed class BackupPostureModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "backup.posture",
        Name = "Backup & recovery readiness",
        Description = "Whether anything is protecting your files: System Restore points, shadow copies, File History, and OneDrive folder backup.",
        Reads = ["System Restore points (WMI)", "Shadow copies (WMI)", "File History configuration", "OneDrive folder redirection (registry)"],
        NeedsAdmin = true,
        EstimatedDuration = TimeSpan.FromSeconds(8),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var anyProtection = false;

        // System Restore points (system settings/drivers — not personal files).
        var restorePoints = Wmi.TryQuery(@"root\default",
            "SELECT CreationTime, Description FROM SystemRestore", out var restoreError);
        if (restoreError is not null) notes.Add($"System Restore query: {restoreError}");

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

        // Volume Shadow Copies (previous versions of files).
        var shadows = Wmi.TryQuery(@"root\cimv2", "SELECT ID, InstallDate FROM Win32_ShadowCopy", out var shadowError);
        if (shadowError is not null) notes.Add($"Shadow copy query: {shadowError}");
        if (shadows.Count > 0) anyProtection = true;
        metrics.Add(new Metric("backup.shadow_copies", "Shadow copies (previous versions)", shadows.Count,
            HealthCategory.Storage, InfoOnly: true,
            Detail: shadows.Count > 0
                ? "Previous versions of files are available for recovery on this PC."
                : "No shadow copies — 'Previous versions' recovery is not available."));

        // File History (real file backup, to a second drive).
        var fileHistoryEnabled = IsFileHistoryEnabled(out var fileHistoryDetail);
        if (fileHistoryEnabled) anyProtection = true;
        metrics.Add(new Metric("backup.file_history", "File History backup", fileHistoryEnabled,
            HealthCategory.Storage, Detail: fileHistoryDetail));

        // OneDrive folder backup (cloud copy of Desktop/Documents/Pictures).
        var oneDriveBackup = IsOneDriveFolderBackupActive(out var oneDriveDetail);
        if (oneDriveBackup) anyProtection = true;
        metrics.Add(new Metric("backup.onedrive_folders", "OneDrive folder backup", oneDriveBackup,
            HealthCategory.Storage, InfoOnly: true, Detail: oneDriveDetail));

        metrics.Add(new Metric("backup.any_protection", "Your files are backed up somewhere", anyProtection,
            HealthCategory.Storage,
            Detail: anyProtection
                ? "At least one form of file protection is active. Check that it covers the folders you actually care about."
                : "Nothing on this PC is backing up your personal files. A drive failure, ransomware attack, or theft would lose them permanently."));

        return Task.FromResult(ModuleStatus.Completed);
    }

    private static bool IsFileHistoryEnabled(out string detail)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "FileHistory", "Configuration");
        var configured = Directory.Exists(configDir) &&
                         Directory.EnumerateFiles(configDir, "Config*.xml").Any();

        var serviceRow = Wmi.TryQuery(@"root\cimv2",
            "SELECT StartMode FROM Win32_Service WHERE Name = 'fhsvc'", out _).FirstOrDefault();
        var startMode = serviceRow is null ? null : Wmi.GetString(serviceRow, "StartMode");
        var serviceReady = startMode is not null && !startMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

        detail = configured
            ? "File History is configured. Confirm its target drive is still connected — File History silently stops when the drive is unplugged."
            : "File History is not set up. It copies your libraries to a second drive automatically (Control Panel > File History).";
        return configured && serviceReady;
    }

    private static bool IsOneDriveFolderBackupActive(out string detail)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
        var redirected = new List<string>();
        foreach (var (valueName, label) in new[] { ("Personal", "Documents"), ("Desktop", "Desktop"), ("My Pictures", "Pictures") })
        {
            if (key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string path &&
                path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                redirected.Add(label);
            }
        }

        detail = redirected.Count > 0
            ? $"OneDrive is backing up: {string.Join(", ", redirected)}. Note that cloud sync mirrors deletions — it is not a substitute for a real backup."
            : "Desktop/Documents/Pictures are not backed up to OneDrive.";
        return redirected.Count > 0;
    }
}
