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
    public void Files_left_by_other_instances_are_reclaimed_too()
    {
        // Each copy of the app writes its own bench-<pid>.tmp so it cannot delete another's
        // mid-measurement. The sweep therefore has to match all of them.
        Directory.CreateDirectory(Folder);
        var mine = Path.Combine(Folder, BenchmarkGate.TestFileNameFor(1234));
        var theirs = Path.Combine(Folder, BenchmarkGate.TestFileNameFor(5678));
        File.WriteAllText(mine, "filler");
        File.WriteAllText(theirs, "filler");

        var removed = BenchmarkCleanup.SweepOrphans([_root]);

        Assert.Equal(2, removed.Count);
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void The_wildcard_still_cannot_reach_anything_but_a_test_file()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, BenchmarkGate.TestFileNameFor(1234)), "filler");
        var theirs = Path.Combine(Folder, "benchmarks-i-wrote.txt");
        File.WriteAllText(theirs, "mine");

        BenchmarkCleanup.SweepOrphans([_root]);

        Assert.True(File.Exists(theirs));
        Assert.True(Directory.Exists(Folder));
    }

    [Fact]
    public void Only_one_copy_can_hold_the_benchmark_gate()
    {
        using var first = BenchmarkGate.TryEnter(_root);
        Assert.NotNull(first);

        // A second copy must be turned away rather than left to compete for the drives.
        Assert.Null(BenchmarkGate.TryEnter(_root));
    }

    [Fact]
    public void The_gate_is_available_again_once_released()
    {
        using (var first = BenchmarkGate.TryEnter(_root)) { Assert.NotNull(first); }

        using var second = BenchmarkGate.TryEnter(_root);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task The_gate_releases_from_a_different_thread_than_took_it()
    {
        // The reason this is not a mutex. The benchmark is async, so the code that releases
        // the gate can be running on a different thread pool thread than the code that took
        // it. A thread-owned lock throws there, stays held, and every later scan in that
        // session silently skips the benchmark.
        var gate = await Task.Run(() => BenchmarkGate.TryEnter(_root));
        Assert.NotNull(gate);

        await Task.Yield();
        await Task.Run(() => gate.Dispose());

        using var again = BenchmarkGate.TryEnter(_root);
        Assert.NotNull(again);
    }

    [Fact]
    public void Each_instance_writes_a_file_named_after_itself()
    {
        Assert.NotEqual(BenchmarkGate.TestFileNameFor(1234), BenchmarkGate.TestFileNameFor(5678));
        Assert.StartsWith("bench-", BenchmarkGate.TestFileNameFor(1234), StringComparison.Ordinal);
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
