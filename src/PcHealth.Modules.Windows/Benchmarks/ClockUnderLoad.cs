namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>
/// How fast the processor actually ran while the CPU benchmark kept one core busy, as a
/// percentage of its base clock. A healthy processor goes well above 100% here (turbo); one that
/// stays below its base clock under load is holding itself back, usually for heat or power. On a
/// ThinkPad T470p with dried-out cooling, a build ran the clock at 67-84% of base (HW-001); an
/// idle scan cannot see that, which is why this rides on the benchmark's load.
///
/// Each sample is the highest value across logical processors, because the busy core is the
/// one that matters and on processors with per-core clocks an average would mix it with idle
/// cores running slowly on purpose.
/// </summary>
public static class ClockUnderLoad
{
    /// <summary>Fewer usable samples than this and there is no honest figure to report.</summary>
    public const int MinimumSamples = 5;

    /// <summary>The median of the per-sample maxima, or null when there are too few to trust.</summary>
    public static double? Typical(IReadOnlyList<double> perSampleMaxima)
    {
        // The first sample is dropped: the formatted counter is computed against the previous
        // read, and the benchmark has only just started loading the core.
        var usable = perSampleMaxima.Skip(1).Where(s => s is > 0 and < 400).Order().ToList();
        if (usable.Count < MinimumSamples) return null;
        var mid = usable.Count / 2;
        var median = usable.Count % 2 == 1 ? usable[mid] : (usable[mid - 1] + usable[mid]) / 2;
        return Math.Round(median);
    }
}
