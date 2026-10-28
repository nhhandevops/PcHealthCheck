using PcHealth.Core.Metrics;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// "Your files are backed up somewhere", from three readings that live in two processes since
/// REMEDIES-PLAN Step A row 7. File History and OneDrive folder backup belong to an account, so
/// they are read in the normal app, for the signed-in account. Shadow copies can only be counted
/// with administrator rights, so the elevated helper counts them, and this conclusion is drawn
/// after the helper has finished (<see cref="PcHealth.Core.Modules.ModuleManifest.RunsAfterOthers"/>).
///
/// "Nothing on this PC is backing up your personal files" is the heaviest sentence this app
/// prints. Finding one working backup proves the positive whatever else broke or did not run. The
/// negative is only earned when all three were read; otherwise there is no conclusion, and a Note
/// says why. The reads that broke were already recorded as failures where they broke, so the Note
/// is not one.
/// </summary>
public static class BackupProtection
{
    public const string MetricId = "backup.any_protection";

    /// <summary>The admin helper's count of shadow copies, which this conclusion looks for among the earlier findings.</summary>
    public const string ShadowCopiesMetricId = "backup.shadow_copies";

    public const string ReadsFailedNote = "Because the checks above could not all finish, this scan cannot say whether anything " +
                                          "is backing up your files. Treat that as unknown, not as 'no backup'.";

    public const string ShadowCopiesNotCountedNote =
        "File History and OneDrive folder backup are both off for this account. Shadow copies are counted only by the " +
        "administrator check \"Restore points & shadow copies\", and it did not count them in this scan, so whether " +
        "anything is backing up your files is not concluded here.";

    /// <summary>
    /// Which of this account's Desktop, Documents and Pictures OneDrive backs up: those inside the
    /// folder of a OneDrive account signed in for it. A path containing "OneDrive" was taken as
    /// proof before the review of 2026-10-28, so a folder left in a OneDrive folder after OneDrive was
    /// signed out or removed (removing it does not move the folders back), or a local folder named
    /// "OneDriveOld", read "Your files are backed up somewhere: Good" with nothing backing them up.
    /// </summary>
    /// <param name="folders">Each folder's label and its path, environment variables expanded.</param>
    /// <param name="accountFolders">The folder of each OneDrive account signed in for this account.</param>
    public static (bool BackedUp, string Detail) OneDriveFolders(
        IReadOnlyList<(string Label, string? Path)> folders, IReadOnlyCollection<string> accountFolders)
    {
        var roots = accountFolders.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.TrimEnd('\\', '/') + @"\").ToList();
        bool Inside(string path) => roots.Any(root => (path.TrimEnd('\\', '/') + @"\").StartsWith(root, StringComparison.OrdinalIgnoreCase));

        var backedUp = folders.Where(f => f.Path is { } p && Inside(p)).Select(f => f.Label).ToList();
        if (backedUp.Count > 0)
        {
            return (true, $"OneDrive is backing up this account's {string.Join(", ", backedUp)}. " +
                          "Note that cloud sync mirrors deletions — it is not a substitute for a real backup.");
        }

        var namedOnly = folders.Where(f => f.Path?.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) == true)
            .Select(f => f.Label).ToList();
        if (namedOnly.Count > 0)
        {
            var one = namedOnly.Count == 1;
            return (false, $"This account's {string.Join(", ", namedOnly)} {(one ? "is" : "are")} in a folder named OneDrive, " +
                           "but no OneDrive account signed in for this account holds that folder, so OneDrive is not " +
                           $"backing {(one ? "it" : "them")} up.");
        }
        return (false, "This account's Desktop, Documents and Pictures are not backed up to OneDrive.");
    }

    /// <param name="fileHistory">Null when File History could not be read.</param>
    /// <param name="oneDrive">Whether OneDrive backs up Desktop, Documents or Pictures.</param>
    /// <param name="shadowCopies">The helper's count, or null when it did not count them in this scan.</param>
    /// <param name="readsFailed">Whether a read of this account's backups broke.</param>
    public static (Metric? Conclusion, string? Note) Conclude(bool? fileHistory, bool oneDrive, double? shadowCopies, bool readsFailed)
    {
        if (fileHistory is true || oneDrive || shadowCopies > 0)
        {
            return (new Metric(MetricId, "Your files are backed up somewhere", true, HealthCategory.Storage,
                Detail: "At least one form of file protection is active. Check that it covers the folders you actually care about."), null);
        }
        if (readsFailed || fileHistory is null) return (null, ReadsFailedNote);
        if (shadowCopies is null) return (null, ShadowCopiesNotCountedNote);
        return (new Metric(MetricId, "Your files are backed up somewhere", false, HealthCategory.Storage,
            Detail: "Nothing on this PC is backing up your personal files. A drive failure, ransomware attack, or theft would lose them permanently. " +
                    "File History and OneDrive were checked for the account this scan ran as."), null);
    }
}
