using System.Globalization;

namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>
/// Reads the Windows error code out of a failed benchmark and says what it means.
///
/// A benchmark that cannot finish is not a missing number — it is a finding, and usually a
/// more serious one than any speed would have been. A drive that fails to complete a write
/// is telling you something the speed columns never could.
///
/// Found during acceptance testing: benchmarking a 2.5" drive in a USB caddy produced
/// "Error while writng during file creation (error code: 1117)" — DiskSpd's own typo
/// included. Correct behaviour from the app, which reported the check as unfinished, but the
/// user is left holding a raw tool message and a number nobody knows.
///
/// Only codes that mean something specific about the hardware are translated. Anything else
/// is passed through untouched rather than dressed up in a guess.
/// </summary>
public static class DiskErrorHints
{
    private static readonly (int Code, string Meaning)[] KnownCodes =
    [
        (1117, "the drive did not complete the write — a device I/O error. This is the drive, its cable, or " +
               "its enclosure failing under sustained load, and it matters far more than any speed reading. " +
               "BACK THIS DRIVE UP. Then try a different cable and a port directly on the motherboard; if it " +
               "keeps happening, replace the drive."),
        (23, "the data came back corrupted — a CRC error. Almost always a cable or connector fault rather than " +
             "the drive itself. BACK IT UP, then replace the cable before anything else."),
        (21, "the drive was not ready. On an external drive this usually means it went to sleep or lost power " +
             "mid-transfer, which a bus-powered drive on a weak port does often."),
        (433, "the drive disappeared during the test. If it is external, it disconnected — check the cable and " +
              "whether the port supplies enough power."),
        (112, "the drive ran out of space during the test, so nothing was measured."),
        (1450, "Windows ran short of system resources during the test rather than the drive failing. Re-run it " +
               "with fewer programs open."),
    ];

    /// <summary>
    /// A plain sentence for the user, or null when the failure says nothing specific about
    /// the hardware and the raw message is the honest thing to show.
    /// </summary>
    public static string? Explain(string? toolOutput)
    {
        if (string.IsNullOrWhiteSpace(toolOutput)) return null;

        foreach (var (code, meaning) in KnownCodes)
        {
            // DiskSpd writes "(error code: 1117)"; matching the number with its parenthesis
            // keeps a stray "1117" elsewhere in the output from being read as an error.
            if (toolOutput.Contains($"error code: {code.ToString(CultureInfo.InvariantCulture)})", StringComparison.OrdinalIgnoreCase))
            {
                return $"What that means: {meaning}";
            }
        }

        return null;
    }
}
