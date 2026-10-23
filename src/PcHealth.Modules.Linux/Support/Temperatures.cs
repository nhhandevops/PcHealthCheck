using System.Globalization;

namespace PcHealth.Modules.Linux.Support;

/// <summary>One temperature input of a hwmon chip: temp1_input and its optional temp1_label.</summary>
internal sealed record HwmonTemperature(string? Label, double Celsius);

/// <summary>One /sys/class/hwmon/hwmonN directory: the driver's name and what it measures.</summary>
internal sealed record HwmonChip(string Dir, string Name, string? Device, IReadOnlyList<HwmonTemperature> Temperatures);

/// <summary>One /sys/class/thermal/thermal_zoneN: its type (x86_pkg_temp, acpitz...) and reading.</summary>
internal sealed record ThermalZone(string Dir, string Type, double Celsius);

/// <summary>A temperature picked for the report, and where it came from, for the Detail text.</summary>
internal sealed record PickedTemperature(string Subject, double Celsius, string Source);

/// <summary>
/// Choosing which of the kernel's many sensors answers "how hot is the processor", and which
/// belong to the drives and the graphics card. Pure, so every vendor's naming is tested on
/// fixture text: a development machine only ever has one of them.
/// </summary>
internal static class Temperatures
{
    /// <summary>
    /// The thermal-zone bounds the Windows module uses, for the same reason: many firmwares expose
    /// a fixed placeholder around 27-30 °C that never moves, and grading it as "cool" would pass
    /// off a reading nobody took as healthy.
    /// </summary>
    public const double ZoneMinPlausibleC = 35;
    public const double ZoneMaxPlausibleC = 125;

    /// <summary>
    /// A processor's own sensor is trusted down to 1 °C. Below that, or above 125 °C, the value is
    /// a driver's way of saying "no reading" (0, -128, 255), not a temperature.
    /// </summary>
    public const double SensorMinPlausibleC = 1;
    public const double SensorMaxPlausibleC = 125;

    /// <summary>sysfs temperatures are millidegrees Celsius as an integer: "45000" is 45 °C.</summary>
    public static double? FromMilli(string? raw) =>
        long.TryParse(raw?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milli)
            ? milli / 1000.0
            : null;

    /// <summary>
    /// The processor's temperature from its own sensor, best source first: Intel's coretemp
    /// (the package, or the hottest core when no package is labelled), AMD's k10temp or
    /// zenpower (Tdie, which has Tctl's fan-curve offset removed, else Tctl), then the
    /// x86_pkg_temp or cpu-thermal zones. Null when the processor has no readable sensor of its own.
    /// </summary>
    public static PickedTemperature? ProcessorSensor(IReadOnlyList<HwmonChip> chips, IReadOnlyList<ThermalZone> zones)
    {
        static bool Plausible(double c) => c is >= SensorMinPlausibleC and <= SensorMaxPlausibleC;

        foreach (var chip in chips.Where(c => c.Name == "coretemp"))
        {
            var readings = chip.Temperatures.Where(t => Plausible(t.Celsius)).ToList();
            var package = readings.Where(t => t.Label?.StartsWith("Package id", StringComparison.Ordinal) == true).ToList();
            var cores = readings.Where(t => t.Label?.StartsWith("Core ", StringComparison.Ordinal) == true).ToList();
            var pick = package.Count > 0 ? package : cores.Count > 0 ? cores : readings;
            if (pick.Count > 0)
                return new PickedTemperature("Processor", pick.Max(t => t.Celsius),
                    $"coretemp, {(package.Count > 0 ? "package" : cores.Count > 0 ? "hottest core" : "hottest input")}");
        }

        foreach (var chip in chips.Where(c => c.Name is "k10temp" or "zenpower"))
        {
            var readings = chip.Temperatures.Where(t => Plausible(t.Celsius)).ToList();
            var pick = readings.FirstOrDefault(t => t.Label == "Tdie") ?? readings.FirstOrDefault(t => t.Label == "Tctl");
            if (pick is not null)
                return new PickedTemperature("Processor", pick.Celsius, $"{chip.Name}, {pick.Label}");
        }

        var zone = zones.Where(z => z.Type is "x86_pkg_temp" or "cpu-thermal" or "cpu_thermal" && Plausible(z.Celsius))
            .MaxBy(z => z.Celsius);
        return zone is null ? null : new PickedTemperature("Processor", zone.Celsius, $"thermal zone {zone.Type}");
    }

    /// <summary>
    /// The fallback when the processor has no sensor of its own: the firmware's ACPI zone, which
    /// sits near the processor. Only readings inside the plausible band count.
    /// </summary>
    public static PickedTemperature? AcpiZone(IReadOnlyList<ThermalZone> zones)
    {
        var zone = zones.Where(z => z.Type == "acpitz" && z.Celsius is >= ZoneMinPlausibleC and <= ZoneMaxPlausibleC)
            .MaxBy(z => z.Celsius);
        return zone is null ? null : new PickedTemperature("ACPI thermal zone", zone.Celsius, $"thermal zone acpitz ({zone.Dir})");
    }

    /// <summary>NVMe drives' own "Composite" temperature, one per drive. Readable without root, unlike SMART.</summary>
    public static List<PickedTemperature> NvmeDrives(IReadOnlyList<HwmonChip> chips) =>
    [
        .. chips.Where(c => c.Name == "nvme")
            .Select(c => (Chip: c, Reading: c.Temperatures.FirstOrDefault(t => t.Label == "Composite")
                                           ?? (c.Temperatures.Count == 1 ? c.Temperatures[0] : null)))
            .Where(x => x.Reading is not null && x.Reading.Celsius is >= SensorMinPlausibleC and <= SensorMaxPlausibleC)
            .Select(x => new PickedTemperature(x.Chip.Device ?? x.Chip.Dir, x.Reading!.Celsius, $"nvme, {x.Reading.Label ?? "temp1"}")),
    ];

    /// <summary>
    /// Graphics temperatures as (metric id, label, reading). amdgpu labels edge, junction (the hot
    /// spot) and mem; nouveau has a single unlabelled input. NVIDIA's own driver publishes no
    /// hwmon sensor, so an NVIDIA card on that driver is simply not covered.
    /// </summary>
    public static List<(string MetricId, string Label, PickedTemperature Reading)> Graphics(IReadOnlyList<HwmonChip> chips)
    {
        var found = new List<(string, string, PickedTemperature)>();
        var gpus = chips.Where(c => c.Name is "amdgpu" or "nouveau").ToList();
        for (var i = 0; i < gpus.Count; i++)
        {
            var chip = gpus[i];
            var subject = (chip.Name == "amdgpu" ? "AMD graphics" : "NVIDIA graphics (nouveau)") + (gpus.Count > 1 ? $" {i + 1}" : "");
            void Add(string metricId, string label, HwmonTemperature? t)
            {
                if (t is not null && t.Celsius is >= SensorMinPlausibleC and <= SensorMaxPlausibleC)
                    found.Add((metricId, label, new PickedTemperature(subject, t.Celsius, $"{chip.Name}, {t.Label ?? "temp1"}")));
            }
            if (chip.Name == "amdgpu")
            {
                Add("gpu.temperature_c", "GPU temperature", chip.Temperatures.FirstOrDefault(t => t.Label == "edge"));
                Add("gpu.hotspot_c", "GPU hot spot", chip.Temperatures.FirstOrDefault(t => t.Label == "junction"));
                Add("gpu.memory_junction_c", "GPU memory", chip.Temperatures.FirstOrDefault(t => t.Label == "mem"));
            }
            else
            {
                Add("gpu.temperature_c", "GPU temperature", chip.Temperatures.Count > 0 ? chip.Temperatures[0] : null);
            }
        }
        return found;
    }
}
