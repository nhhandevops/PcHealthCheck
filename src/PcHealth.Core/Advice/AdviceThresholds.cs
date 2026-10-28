namespace PcHealth.Core.Advice;

/// <summary>
/// The only numbers advice adds to the rule files (STEP-B-SPEC 3.2). Every other test an item makes
/// is a reading's own grade, so a threshold changed in a rule file changes the advice with it. Each
/// one here is tested on both sides.
/// </summary>
public static class AdviceThresholds
{
    /// <summary>
    /// S1 suggests a restart only after Windows has been up for more than this many days. Uptime comes
    /// from LastBootUpTime, which Fast Startup's "Shut down" does not reset. The uptime row itself is
    /// graded Good up to 30 days, so this alone never makes a card: a pending restart or a high
    /// memory commit must be graded too.
    /// </summary>
    public const double RestartAfterDays = 7;

    /// <summary>
    /// H1's replacement drive: at least this many times the space in use, for room to grow and
    /// because an SSD that is nearly full slows down and wears faster.
    /// </summary>
    public const double ReplacementFactor = 1.5;

    /// <summary>
    /// H1's replacement drive is the smallest of these common SSD sizes that is large enough. They are
    /// the sizes on the box, in GB of 10^9 bytes: a "256 GB" SSD holds 238.4 GB as Windows counts
    /// them, in 1024^3 bytes, the unit of the space in use.
    /// </summary>
    public static readonly IReadOnlyList<int> ReplacementSizesGb = [128, 256, 512, 1000, 2000, 4000];

    /// <summary>Bytes in a GB as Windows and the space-in-use row count it (1024^3), over bytes in a GB on an SSD's box (10^9).</summary>
    private const double WindowsGbInBoxGb = 1073741824.0 / 1e9;

    /// <summary>
    /// H1's copy-time estimate: a USB 3 external drive typically writes 50 to 100 MB/s over a whole
    /// copy of many files, and the card says "about" and "typical" (REMEDIES-PLAN principle 13).
    /// </summary>
    public const double SlowCopyMbPerSecond = 50, FastCopyMbPerSecond = 100;

    /// <summary>Estimated minutes are rounded up to this, so "about 15 minutes" is not read as a measurement.</summary>
    public const double MinutesStep = 5;

    /// <summary>
    /// The smallest standard size of at least 1.5 times the space in use, or null when even the
    /// largest is too small. The space in use is in Windows' GB (1024^3 bytes) and is turned into
    /// the box's GB first, or a drive picked "at 1.5 times" would hold up to 7% less.
    /// </summary>
    public static int? ReplacementSizeGb(double usedGb)
    {
        var needed = usedGb * WindowsGbInBoxGb * ReplacementFactor;
        foreach (var size in ReplacementSizesGb)
        {
            if (size >= needed) return size;
        }
        return null;
    }

    /// <summary>Minutes to copy this many GB at this many MB/s, rounded up to <see cref="MinutesStep"/>.</summary>
    public static double CopyMinutes(double usedGb, double mbPerSecond) =>
        RoundUpMinutes(usedGb * 1024 / mbPerSecond / 60);

    /// <summary>Rounds up to the next multiple of <see cref="MinutesStep"/>; a whole multiple stays as it is.</summary>
    public static double RoundUpMinutes(double minutes) => Math.Ceiling(minutes / MinutesStep) * MinutesStep;
}
