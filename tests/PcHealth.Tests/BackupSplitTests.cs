using PcHealth.Core.Metrics;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows;
using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// REMEDIES-PLAN Step A row 7: File History and OneDrive are read in the normal app, for the
/// signed-in account, and no longer by the admin helper, which runs as whoever typed the
/// administrator password. "Your files are backed up somewhere" keeps its grade wherever all
/// three readings were made, and is not concluded where one was not: from a read that broke, or
/// from shadow copies the admin check did not count in this scan.
/// </summary>
public sealed class BackupSplitTests
{
    private static readonly VerdictEngine Engine = new(RulesLoader.LoadEmbedded("windows"));

    private static Grade GradeOf(Metric? metric) => Engine.EvaluateOne(metric!).Grade;

    // --- Where each reading runs ----------------------------------------------------------------

    [Fact]
    public void File_backup_runs_without_admin_after_the_helper_and_restore_points_stay_in_the_helper()
    {
        var modules = new WindowsModuleProvider().CreateModules().ToDictionary(m => m.Manifest.Id, StringComparer.Ordinal);

        var files = modules["backup.files"].Manifest;
        Assert.False(files.NeedsAdmin);
        Assert.True(files.RunsAfterOthers);
        Assert.False(files.RunsAlone);

        var posture = modules["backup.posture"].Manifest;
        Assert.True(posture.NeedsAdmin);
        Assert.DoesNotContain(posture.Reads, r => r.Contains("File History", StringComparison.Ordinal) || r.Contains("OneDrive", StringComparison.Ordinal));
    }

    // --- The positive: one working backup proves it, whatever else broke or did not run ------

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(false, false, 3.0)]
    [InlineData(null, true, null)] // File History could not be read, OneDrive still proves it
    public void One_working_backup_is_enough(bool? fileHistory, bool oneDrive, double? shadowCopies)
    {
        var (conclusion, note) = BackupProtection.Conclude(fileHistory, oneDrive, shadowCopies, readsFailed: fileHistory is null);

        Assert.Equal(true, conclusion!.Value.Flag);
        Assert.Equal(Grade.Good, GradeOf(conclusion));
        Assert.Null(note);
    }

    // --- The negative: earned only when all three were read -----------------------------------

    [Fact]
    public void Nothing_on_when_all_three_were_read_is_still_Bad()
    {
        var (conclusion, note) = BackupProtection.Conclude(fileHistory: false, oneDrive: false, shadowCopies: 0, readsFailed: false);

        Assert.Equal(false, conclusion!.Value.Flag);
        Assert.Equal(Grade.Bad, GradeOf(conclusion));
        Assert.Contains("for the account this scan ran as", conclusion.Detail!, StringComparison.Ordinal);
        Assert.Null(note);
    }

    [Fact]
    public void Shadow_copies_the_admin_check_did_not_count_leave_it_unconcluded_with_a_note()
    {
        // An unelevated scan, or a helper that was declined: deliberately not checked, so a Note.
        var (conclusion, note) = BackupProtection.Conclude(fileHistory: false, oneDrive: false, shadowCopies: null, readsFailed: false);

        Assert.Null(conclusion);
        Assert.Equal(BackupProtection.ShadowCopiesNotCountedNote, note);
        Assert.DoesNotContain("; ", note, StringComparison.Ordinal); // the kit splits a row's notes on it
    }

    [Fact]
    public void A_read_that_broke_leaves_it_unknown_not_no_backup()
    {
        var (conclusion, note) = BackupProtection.Conclude(fileHistory: null, oneDrive: false, shadowCopies: 0, readsFailed: true);

        Assert.Null(conclusion);
        Assert.Equal(BackupProtection.ReadsFailedNote, note);
    }

    // --- OneDrive: a folder named OneDrive is not OneDrive backing it up (review of 2026-10-28) -

    private const string Profile = @"C:\Users\Example";

    private static (string Label, string? Path)[] Folders(string documents, string? desktop = null) =>
        [("Documents", documents), ("Desktop", desktop ?? Profile + @"\Desktop"), ("Pictures", Profile + @"\Pictures")];

    [Theory]
    [InlineData(@"C:\Users\Example\OneDrive", @"C:\Users\Example\OneDrive\Documents")]
    [InlineData(@"C:\Users\Example\OneDrive - Example Org\", @"c:\users\example\onedrive - example org\Documents")]
    [InlineData(@"D:\Cloud", @"D:\Cloud\Documents")] // an account whose folder is not named OneDrive
    public void A_folder_inside_a_signed_in_onedrive_accounts_folder_is_backed_up(string account, string documents)
    {
        var (backedUp, detail) = BackupProtection.OneDriveFolders(Folders(documents), [account]);

        Assert.True(backedUp);
        Assert.Equal("OneDrive is backing up this account's Documents. Note that cloud sync mirrors deletions — it is not a " +
                     "substitute for a real backup.", detail);
        Assert.Equal(Grade.Good, GradeOf(BackupProtection.Conclude(false, backedUp, 0, readsFailed: false).Conclusion));
    }

    [Theory]
    [InlineData(new string[0], @"C:\Users\Example\OneDrive\Documents")]  // OneDrive signed out or removed: the redirect stays
    [InlineData(new[] { @"C:\Users\Example\OneDrive" }, @"D:\OneDriveOld\Documents")]
    [InlineData(new[] { @"C:\Users\Example\OneDrive" }, @"C:\Users\Example\OneDrive - Example Org\Documents")] // that account is gone
    public void A_folder_named_onedrive_that_no_signed_in_account_holds_is_not_backed_up(string[] accounts, string documents)
    {
        var (backedUp, detail) = BackupProtection.OneDriveFolders(Folders(documents), accounts);

        Assert.False(backedUp);
        Assert.Equal("This account's Documents is in a folder named OneDrive, but no OneDrive account signed in for this " +
                     "account holds that folder, so OneDrive is not backing it up.", detail);
        // With File History off and no shadow copies, nothing is backing the files up.
        Assert.Equal(Grade.Bad, GradeOf(BackupProtection.Conclude(false, backedUp, 0, readsFailed: false).Conclusion));
    }

    [Fact]
    public void Folders_where_windows_put_them_are_not_backed_up_to_onedrive()
    {
        var (backedUp, detail) = BackupProtection.OneDriveFolders(Folders(Profile + @"\Documents"), [Profile + @"\OneDrive"]);

        Assert.False(backedUp);
        Assert.Equal("This account's Desktop, Documents and Pictures are not backed up to OneDrive.", detail);
    }

    [Fact]
    public void Only_the_folders_a_signed_in_account_holds_are_named()
    {
        var (backedUp, detail) = BackupProtection.OneDriveFolders(
            Folders(Profile + @"\OneDrive\Documents", desktop: @"E:\OneDrive\Desktop"), [Profile + @"\OneDrive"]);

        Assert.True(backedUp);
        Assert.StartsWith("OneDrive is backing up this account's Documents.", detail, StringComparison.Ordinal);
    }
}
