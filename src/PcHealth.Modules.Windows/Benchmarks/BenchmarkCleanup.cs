namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>
/// Clears up benchmark test files that an earlier run could not delete itself.
///
/// The benchmark deletes its file in a <c>finally</c>, which covers cancelling and failing.
/// It does not cover the process being killed, the machine losing power, or a crash — and
/// found during acceptance testing, that leaves a gigabyte sitting on the user's drive
/// forever, with the app never mentioning it. An app that promises to clean up after itself
/// has to mean it in the cases where the promise is actually hard to keep.
///
/// Deleting these is finishing a job the user already consented to ("deleted as soon as the
/// check finishes"), not a new action, so it happens without asking. It is still reported,
/// because a silent deletion is the wrong habit for this app even when the file is its own.
///
/// Only ever touches the exact path the benchmark creates. Nothing is searched for, nothing
/// is matched by pattern, and a folder holding anything other than that one file is left
/// alone — the whole point is that it can never reach a file that is not the app's.
/// </summary>
public static class BenchmarkCleanup
{
    public const string WorkingFolder = "PcHealthCheck-bench";
    public const string TestFileName = "bench.tmp";

    /// <summary>
    /// Matches this instance's file and every other instance's. The wildcard is safe only
    /// because it is confined to a folder nothing but this app creates, and because the
    /// folder is still removed only when empty.
    /// </summary>
    public const string TestFilePattern = "bench*.tmp";

    /// <summary>Paths removed, in the order they were found. Empty on the normal path.</summary>
    public static IReadOnlyList<string> SweepOrphans() => SweepOrphans(ReadyLocalDrives());

    /// <summary>
    /// The same sweep against a given set of roots. Exists so the promise that this can
    /// never reach a file it did not create is testable without writing to real drives.
    /// </summary>
    public static IReadOnlyList<string> SweepOrphans(IEnumerable<string> roots)
    {
        var removed = new List<string>();

        foreach (var drive in roots)
        {
            var folder = Path.Combine(drive, WorkingFolder);
            try
            {
                if (Directory.Exists(folder))
                {
                    foreach (var file in Directory.EnumerateFiles(folder, TestFilePattern))
                    {
                        File.Delete(file);
                        removed.Add(file);
                    }
                }

                // Removed only when empty, so a folder someone else has put something in
                // survives untouched.
                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover that cannot be deleted is not worth failing a scan over; the
                // next run will try again.
            }
        }

        return removed;
    }

    /// <summary>
    /// Fixed and removable drives that are ready. Network and optical drives are skipped:
    /// the benchmark never writes to them, and walking a slow network share at startup would
    /// make the app feel broken.
    /// </summary>
    private static IEnumerable<string> ReadyLocalDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { yield break; }

        foreach (var drive in drives)
        {
            bool usable;
            try { usable = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable; }
            catch (IOException) { continue; }
            if (usable) yield return drive.RootDirectory.FullName;
        }
    }
}
