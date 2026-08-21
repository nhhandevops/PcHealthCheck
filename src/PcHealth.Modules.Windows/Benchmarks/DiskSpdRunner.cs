using System.Globalization;
using PcHealth.Core.Processes;

namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>Which shape of access is being measured.</summary>
public enum DiskProfile
{
    /// <summary>Long transfers, one thread — copying a film, loading a game.</summary>
    Sequential,

    /// <summary>4 KB reads scattered across the file at high queue depth — what makes a PC feel quick.</summary>
    Random4K,
}

/// <summary>
/// Drives Microsoft's DiskSpd (MIT) to measure read speed.
///
/// Read-only by construction: every profile passes <c>-w0</c>, so the measured phase never
/// writes. The one write is DiskSpd creating the test file itself, which is disclosed on the
/// consent row, recorded in the receipt, and deleted when the module finishes.
///
/// The tool is not bundled. It is offered through the dependency manager instead, which
/// means the user sees the install command, it can be removed again on exit, and this repo
/// carries no third-party binaries.
/// </summary>
public sealed class DiskSpdRunner(IProcessRunner processes)
{
    public const string ToolName = "diskspd.exe";

    /// <summary>Big enough that the drive cannot serve it from its own cache.</summary>
    public const long DefaultFileBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// What external drives get instead. Still far larger than any drive cache, so the
    /// measurement stays honest, but a quarter of the sustained write.
    /// </summary>
    public const long ExternalFileBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Creating the test file is the only write this benchmark makes, and on a bus-powered
    /// USB drive it is the part that hurts: measured on real hardware, writing a gigabyte to
    /// a 2.5" drive in a caddy produced 32 Windows I/O retries, while both read passes over
    /// the same file produced none. Without administrator rights DiskSpd cannot simply set
    /// the file's valid length and has to write every byte, so the only lever is to ask for
    /// fewer bytes.
    /// </summary>
    public static long TestFileBytes(DriveClass driveClass) =>
        driveClass == DriveClass.External ? ExternalFileBytes : DefaultFileBytes;

    private const int MeasureSeconds = 10;
    private const int WarmupSeconds = 2;

    public static string? Find() => ToolLocator.Find(ToolName, "Microsoft.DiskSpd");

    /// <summary>
    /// The exact arguments, so the consent row and the receipt can show what will run.
    /// <c>-Sh</c> bypasses both the operating system's cache and the drive's own write
    /// cache, without which this would mostly be measuring RAM.
    ///
    /// <paramref name="deepQueue"/> is the difference between measuring a drive and
    /// bullying it. Piling 128 outstanding 4 KB reads onto a spinning disk — worse, one
    /// behind a USB bridge — makes the bridge time out and Windows log I/O retries. That is
    /// not a fault in the drive, it is a fault in the test, and this app of all things must
    /// not create the errors it goes on to report. Solid-state drives are built for that
    /// depth and are measured at it; everything else is asked politely.
    /// </summary>
    public static string Arguments(DiskProfile profile, string filePath, long? createBytes, bool deepQueue)
    {
        var create = createBytes is { } bytes
            ? $"-c{bytes.ToString(CultureInfo.InvariantCulture)} "
            : "";
        var shape = (profile, deepQueue) switch
        {
            (DiskProfile.Sequential, true) => "-b1M -o8 -t1 -si",   // one thread streaming 1 MB blocks
            (DiskProfile.Sequential, false) => "-b1M -o2 -t1 -si",  // enough to keep a platter busy, no more
            (DiskProfile.Random4K, true) => "-b4K -o32 -t4 -r",     // what an SSD is built for
            _ => "-b4K -o4 -t1 -r",                                // what a head can actually chase
        };
        var duration = $"-d{MeasureSeconds.ToString(CultureInfo.InvariantCulture)} " +
                       $"-W{WarmupSeconds.ToString(CultureInfo.InvariantCulture)}";
        return $"{create}{shape} -w0 -Sh {duration} -L -Rxml \"{filePath}\"";
    }

    /// <summary>
    /// Deep queues suit solid-state drives and nothing else. A hard disk gains nothing from
    /// them because there is one head, and an external enclosure can fail outright under them.
    /// </summary>
    public static bool DeepQueueSuits(DriveClass driveClass) =>
        driveClass is DriveClass.SataSsd or DriveClass.NvmeSsd;

    public async Task<(DiskSpdReading? Reading, string? Error)> MeasureAsync(
        string exePath, DiskProfile profile, string filePath, long? createBytes, bool deepQueue, CancellationToken ct)
    {
        var result = await processes
            .RunAsync("bench.disk", exePath, Arguments(profile, filePath, createBytes, deepQueue),
                TimeSpan.FromMinutes(5), ct: ct)
            .ConfigureAwait(false);

        if (result.TimedOut)
        {
            return (null, "The benchmark did not finish within five minutes and was stopped.");
        }

        // DiskSpd still prints its XML on some non-zero exits, so parse first and only fall
        // back to the exit code when there is genuinely nothing to read.
        var reading = DiskSpdOutputReader.Parse(result.StdOut, out var parseError);
        if (reading is not null) return (reading, null);

        var detail = string.IsNullOrWhiteSpace(result.StdErr) ? parseError : result.StdErr.Trim();
        return (null, $"DiskSpd exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}: {detail}");
    }
}
