using System.Globalization;

namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>
/// Stops two copies of the app benchmarking the same drives at the same time.
///
/// Found in acceptance testing by running two instances at once. Both completed, both
/// reported success, and both recorded roughly half the drive's real speed — because they
/// were competing for it — with nothing in either report to say why. One also failed to
/// delete its own test file, because the other had already removed the folder.
///
/// Half-speed numbers presented as measurements are worse than no numbers: with trend
/// history on, they become the baseline every later run is judged against. So the second
/// instance does not measure at all, and says so.
///
/// **Why a lock file rather than a mutex.** The obvious choice is a named mutex, and it was
/// the first attempt — until a test caught what it would have cost. A mutex is owned by the
/// *thread* that took it, and the benchmark is async: the continuation after the first await
/// can resume on a different thread pool thread, so releasing it there throws and the lock
/// stays held for the life of the process. Every scan after the first in that session would
/// have silently skipped the benchmark. An exclusive file handle belongs to the process, not
/// the thread, and Windows releases it even if the process is killed.
/// </summary>
public sealed class BenchmarkGate : IDisposable
{
    private readonly FileStream? _lock;

    private BenchmarkGate(FileStream? lockFile) => _lock = lockFile;

    /// <summary>
    /// Returns null when another copy is already benchmarking.
    ///
    /// Per-user, because the lock lives in this user's own app folder. Two people signed in
    /// at once could still overlap — rare enough, and far less likely than one person with
    /// two windows open, which is what this is for.
    /// </summary>
    public static BenchmarkGate? TryEnter(string? lockDirectory = null)
    {
        var directory = lockDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHealthCheck");

        try
        {
            Directory.CreateDirectory(directory);
            var stream = new FileStream(
                Path.Combine(directory, "benchmark.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            return new BenchmarkGate(stream);
        }
        catch (IOException)
        {
            // Held by another copy. This is the case the gate exists for.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot create the lock at all. Refusing to benchmark would be worse than the
            // rare overlap it is guarding against, so let the caller proceed unguarded.
            return new BenchmarkGate(lockFile: null);
        }
    }

    public void Dispose() => _lock?.Dispose();

    /// <summary>
    /// Each instance writes its own file, so one cannot delete another's mid-measurement.
    /// The gate makes that collision unlikely; unique names make it impossible.
    /// </summary>
    public static string TestFileNameFor(int processId) =>
        $"bench-{processId.ToString(CultureInfo.InvariantCulture)}.tmp";
}
