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

    private const int MeasureSeconds = 10;
    private const int WarmupSeconds = 2;

    public static string? Find() => ToolLocator.Find(ToolName, "Microsoft.DiskSpd");

    /// <summary>
    /// The exact arguments, so the consent row and the receipt can show what will run.
    /// <c>-Sh</c> bypasses both the operating system's cache and the drive's own write
    /// cache, without which this would mostly be measuring RAM.
    /// </summary>
    public static string Arguments(DiskProfile profile, string filePath, long? createBytes)
    {
        var create = createBytes is { } bytes
            ? $"-c{bytes.ToString(CultureInfo.InvariantCulture)} "
            : "";
        var shape = profile == DiskProfile.Sequential
            ? "-b1M -o8 -t1 -si"       // one thread streaming 1 MB blocks
            : "-b4K -o32 -t4 -r";      // four threads, 32 outstanding 4 KB reads each
        var duration = $"-d{MeasureSeconds.ToString(CultureInfo.InvariantCulture)} " +
                       $"-W{WarmupSeconds.ToString(CultureInfo.InvariantCulture)}";
        return $"{create}{shape} -w0 -Sh {duration} -L -Rxml \"{filePath}\"";
    }

    public async Task<(DiskSpdReading? Reading, string? Error)> MeasureAsync(
        string exePath, DiskProfile profile, string filePath, long? createBytes, CancellationToken ct)
    {
        var result = await processes
            .RunAsync("bench.disk", exePath, Arguments(profile, filePath, createBytes),
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
