using PcHealth.Modules.Windows.Benchmarks;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// The benchmark deletes its test file in a finally, which covers cancelling and failing but
/// not the process being killed or the machine losing power. Acceptance testing found that a
/// hard kill mid-benchmark leaves a gigabyte on the drive with nothing to ever reclaim it.
///
/// The sweep that fixes it deletes files without asking, so what it can reach matters more
/// than what it does.
/// </summary>
public sealed class BenchmarkCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pchealth-sweep-" + Guid.NewGuid().ToString("N")[..8]);

    private string Folder => Path.Combine(_root, BenchmarkCleanup.WorkingFolder);

    private string MakeLeftover()
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, BenchmarkCleanup.TestFileName);
        File.WriteAllText(file, "filler");
        return file;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void A_leftover_test_file_is_reclaimed_and_reported()
    {
        var file = MakeLeftover();

        var removed = BenchmarkCleanup.SweepOrphans([_root]);

        Assert.Equal(file, Assert.Single(removed));
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void Anything_that_is_not_the_apps_own_file_is_left_where_it_is()
    {
        // The claim this sweep makes is that it can never reach a file it did not create.
        // If someone has put something in that folder, both it and the folder survive.
        MakeLeftover();
        var theirs = Path.Combine(Folder, "someone-elses-notes.txt");
        File.WriteAllText(theirs, "important");

        BenchmarkCleanup.SweepOrphans([_root]);

        Assert.True(File.Exists(theirs));
        Assert.Equal("important", File.ReadAllText(theirs));
        Assert.True(Directory.Exists(Folder));
    }

    [Fact]
    public void A_folder_with_the_same_name_but_no_test_file_is_untouched()
    {
        Directory.CreateDirectory(Folder);
        var theirs = Path.Combine(Folder, "readme.txt");
        File.WriteAllText(theirs, "mine");

        Assert.Empty(BenchmarkCleanup.SweepOrphans([_root]));
        Assert.True(File.Exists(theirs));
    }

    [Fact]
    public void A_drive_with_nothing_to_clean_reports_nothing()
    {
        Directory.CreateDirectory(_root);

        Assert.Empty(BenchmarkCleanup.SweepOrphans([_root]));
    }

    [Fact]
    public void A_root_that_is_not_there_is_not_an_error()
    {
        // Drives get unplugged between one run and the next; that must not throw during
        // startup, which is where this now runs.
        Assert.Empty(BenchmarkCleanup.SweepOrphans([Path.Combine(_root, "no-such-drive")]));
    }

    [Fact]
    public void The_real_sweep_runs_against_this_machine_without_throwing()
    {
        // Exercises the actual drive enumeration, including any unreadable or odd volume.
        _ = BenchmarkCleanup.SweepOrphans();
    }
}
