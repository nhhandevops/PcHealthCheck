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
    /// Temperature above which a graphics card's fans really should be turning. Cards
    /// deliberately idle with their fans stopped and only start them in the 55–65 °C
    /// region, so the bar has to sit well above that to avoid crying wolf.
    /// </summary>
    public const double GpuFansExpectedAboveC = 80;

    /// <summary>Processors are cooled continuously, so the bar is lower — but still above
    /// ordinary desktop idle temperatures.</summary>
    public const double CpuFansExpectedAboveC = 70;

    /// <summary>
    /// Sensor names that run far hotter than the component's headline temperature by
    /// design. Judging airflow by these would flag a perfectly cool, idle card.
    /// </summary>
    private static readonly string[] HotByDesignSensors =
        ["Hot Spot", "Hotspot", "Memory Junction", "VRM", "VRAM", "MOSFET"];

    public static bool IsHotByDesignSensor(string sensorName) =>
        HotByDesignSensors.Any(marker => sensorName.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The temperature that represents a device's general state, ignoring the sensors that
    /// are hot by design.
    /// </summary>
    public static SensorReading? SelectRepresentativeTemperature(IReadOnlyList<SensorReading> temperatures)
    {
        var candidates = temperatures.Where(t => !IsHotByDesignSensor(t.Name)).ToList();
        return candidates.Count > 0 ? candidates.MaxBy(t => t.Peak) : null;
    }

    /// <summary>
    /// True only when a device is genuinely hot AND every fan belonging to THAT device is
    /// stopped. Comparing one device's fans against another's temperature (or against a
    /// hot-by-design sensor) produces false alarms on any modern PC.
    /// </summary>
    public static bool IsStoppedFanConcerning(
        IReadOnlyList<SensorReading> fansOnThisDevice,
        double representativeTemperature,
        double fansExpectedAboveC)
    {
        if (fansOnThisDevice.Count == 0) return false;      // no sensors is not evidence of a fault
        if (representativeTemperature < fansExpectedAboveC) return false;
        return fansOnThisDevice.All(f => f.Peak <= 0);
    }

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
        return SelectRepresentativeTemperature(cpuTemperatures) ?? cpuTemperatures.MaxBy(t => t.Peak);
    }
}
