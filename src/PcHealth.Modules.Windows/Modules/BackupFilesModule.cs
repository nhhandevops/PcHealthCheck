using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// File backup for the account the app runs as: File History and OneDrive folder backup, and from
/// them and the admin check's shadow-copy count, whether anything is backing up your files.
///
/// These were read by the elevated helper before Step A, and the helper runs as whoever typed
/// the administrator password. On a company laptop that is the technician, so the report described
/// the technician's File History and OneDrive, not the employee's (REMEDIES-PLAN H1, Step A row 7).
/// Neither needs admin, so they are read here, in the normal app. Restore points and shadow copies
/// cannot be listed without admin (on the dev T470p, unelevated, on 2026-10-28: SystemRestore
/// "Access denied", Win32_ShadowCopy "Initialization failure"), so they stay in
/// <see cref="BackupPostureModule"/>, and this check runs after it to see its count.
/// </summary>
internal sealed class BackupFilesModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "backup.files",
        Name = "File backup",
        Description = "Whether anything is backing up your files: File History and OneDrive folder backup for your account, plus the shadow copies the administrator check counted, when it ran.",
        Reads = ["File History configuration of your account (whether its settings exist)", "File History service start mode (WMI)", "Where your account's Desktop, Documents and Pictures folders are, and the folder of each OneDrive account signed in for it (registry)"],
        NeedsAdmin = false,
        RunsAfterOthers = true, // the conclusion needs the admin helper's shadow-copy count
        EstimatedDuration = TimeSpan.FromSeconds(2),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        // File History (real file backup, to a second drive).
        var fileHistory = IsFileHistoryEnabled(failures, out var fileHistoryDetail);
        if (fileHistory is { } enabled)
        {
            metrics.Add(new Metric("backup.file_history", "File History backup", enabled,
                HealthCategory.Storage, Detail: fileHistoryDetail));
        }

        // OneDrive folder backup (cloud copy of Desktop/Documents/Pictures).
        var oneDrive = IsOneDriveFolderBackupActive(out var oneDriveDetail);
        metrics.Add(new Metric("backup.onedrive_folders", "OneDrive folder backup", oneDrive,
            HealthCategory.Storage, InfoOnly: true, Detail: oneDriveDetail));

        var shadowCopies = context.EarlierFindings
            .FirstOrDefault(v => v.Metric.Id == BackupProtection.ShadowCopiesMetricId)?.Metric.Value.Number;
        var (conclusion, note) = BackupProtection.Conclude(fileHistory, oneDrive, shadowCopies, failures.Any);
        if (conclusion is not null) metrics.Add(conclusion);
        if (note is not null) failures.Note(note);

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    /// <summary>
    /// Null when the File History service could not be read. The answer needs both halves — a
    /// configuration whose service is disabled backs nothing up — so a lost service query cannot
    /// return false: that would report File History as off to someone whose backups are running.
    /// A service row that is simply absent is a real answer (fhsvc does not exist here), not a
    /// failure, and stays false.
    /// </summary>
    private static bool? IsFileHistoryEnabled(ModuleFailures failures, out string detail)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "FileHistory", "Configuration");
        var configured = Directory.Exists(configDir) &&
                         Directory.EnumerateFiles(configDir, "Config*.xml").Any();

        detail = configured
            ? "File History is configured for this account. Confirm its target drive is still connected — File History silently stops when the drive is unplugged."
            : "File History is not set up for this account. It copies your libraries to a second drive automatically (Control Panel > File History).";

        var serviceRow = Wmi.TryQuery(@"root\cimv2",
            "SELECT StartMode FROM Win32_Service WHERE Name = 'fhsvc'", out var serviceError).FirstOrDefault();
        if (serviceError is not null)
        {
            failures.Record("File History service query", serviceError);
            return null;
        }

        var startMode = serviceRow is null ? null : Wmi.GetString(serviceRow, "StartMode");
        var serviceReady = startMode is not null && !startMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
        return configured && serviceReady;
    }

    /// <summary>
    /// The three folders, and the folder of each OneDrive account signed in for this account (each
    /// subkey of OneDrive\Accounts, such as Personal or Business1, names it in UserFolder). Neither
    /// path is written anywhere: <see cref="BackupProtection.OneDriveFolders"/> compares them.
    /// </summary>
    private static bool IsOneDriveFolderBackupActive(out string detail)
    {
        var folders = new List<(string Label, string? Path)>();
        using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
        {
            foreach (var (valueName, label) in new[] { ("Personal", "Documents"), ("Desktop", "Desktop"), ("My Pictures", "Pictures") })
            {
                var path = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                folders.Add((label, path is null ? null : Environment.ExpandEnvironmentVariables(path)));
            }
        }

        var accountFolders = new List<string>();
        using (var accounts = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\OneDrive\Accounts"))
        {
            foreach (var name in accounts?.GetSubKeyNames() ?? [])
            {
                using var account = accounts!.OpenSubKey(name);
                if (account?.GetValue("UserFolder") is string userFolder) accountFolders.Add(userFolder);
            }
        }

        (var backedUp, detail) = BackupProtection.OneDriveFolders(folders, accountFolders);
        return backedUp;
    }
}
