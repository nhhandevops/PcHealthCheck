namespace PcHealth.Modules.Windows.Sensors;

/// <summary>One temperature/fan/load reading, already reduced to the peak seen while sampling.</summary>
public sealed record SensorReading(string Hardware, string Name, double Peak, double Latest);

/// <summary>
/// The derived checks that turn raw sensor numbers into findings. Kept separate from the
/// hardware access so the logic is testable without a kernel driver.
/// </summary>
public static class SensorAnalysis
{
    /// <summary>
    /// A fan reporting zero RPM is normal on a modern PC — most run a "zero-RPM" idle mode.
    /// It is only a problem if something is genuinely hot at the same time.
    /// </summary>
    public const double HotEnoughToExpectAirflowC = 60;

    public static bool IsStoppedFanConcerning(IReadOnlyList<SensorReading> fans, double hottestTemperature) =>
        hottestTemperature >= HotEnoughToExpectAirflowC && fans.Count > 0 && fans.All(f => f.Peak <= 0);

    /// <summary>
    /// Picks the reading that best represents overall package temperature. Vendors name
    /// this differently (Intel "CPU Package", AMD "Core (Tctl/Tdie)"), so prefer the known
    /// names and fall back to the hottest core.
    /// </summary>
    public static SensorReading? SelectPackageTemperature(IReadOnlyList<SensorReading> cpuTemperatures)
    {
        if (cpuTemperatures.Count == 0) return null;

        string[] preferred = ["CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "CPU Cores", "Core Max"];
        foreach (var name in preferred)
        {
            var match = cpuTemperatures.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return cpuTemperatures.MaxBy(t => t.Peak);
    }
}
