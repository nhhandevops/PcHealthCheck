using System.Globalization;

namespace PcHealth.Core.Metrics;

/// <summary>
/// Whether a full-charge-against-design reading is a measurement at all.
/// </summary>
/// <remarks>
/// A pack cannot hold more than it was built to hold. A fresh one measures a little over 100%
/// often enough that the margin has to be generous, but a figure well past that means the gauge
/// has lost its calibration — usually after a long spell without a full discharge, and worst of
/// all when read at a low charge. Such a number says nothing about wear, so it must not be
/// graded: grading it Good reports a battery nobody could measure as being in better shape than
/// one that was measured honestly. Shared by the Windows and Linux battery modules, which derive
/// the same metric from the same ratio and would otherwise need the same guard twice.
/// </remarks>
public static class BatteryCapacity
{
    /// <summary>The highest reading still treated as a real measurement, in percent of design.</summary>
    public const double PlausibleMaxPct = 105;

    /// <summary>True when the reading can be believed and graded.</summary>
    public static bool IsPlausible(double pct) => pct is > 0 and <= PlausibleMaxPct;

    /// <summary>Why an implausible reading is not graded, and how to get a real one.</summary>
    public static string CalibrationAdvice(double pct)
    {
        var shown = pct.ToString("0.#", CultureInfo.InvariantCulture);
        return $"This reads {shown}% of its design capacity, which a battery cannot hold, so the gauge has " +
               "lost its calibration and this figure does not measure wear. It is left ungraded rather than " +
               "counted as healthy. To get a real reading: charge to 100%, use the laptop until it sleeps on " +
               "its own, then charge to 100% again without interruption, and check again.";
    }

    /// <summary>The module note that degrades the scan, so an unmeasured battery is never silently fine.</summary>
    public const string UnreliableNote =
        "The battery gauge reported a full-charge capacity larger than the design capacity, " +
        "so battery wear was NOT established.";
}
