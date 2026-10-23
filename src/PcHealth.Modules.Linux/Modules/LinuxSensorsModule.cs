using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Linux.Support;

namespace PcHealth.Modules.Linux.Modules;

/// <summary>
/// Processor, NVMe drive and graphics temperatures from the kernel's own sensor drivers. On
/// Windows the same readings need a kernel driver loaded by an administrator; on Linux the
/// drivers are part of the kernel and their readings are ordinary files, so this needs neither.
/// </summary>
internal sealed class LinuxSensorsModule(SystemFiles files, int samples, TimeSpan interval) : HealthModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "linux.sensors",
        Name = "Temperatures",
        Description = "Processor, NVMe drive and graphics temperatures from the kernel's own sensors, highest of three readings a second apart. Loads nothing and needs no administrator rights.",
        Reads =
        [
            "Temperature sensors (/sys/class/hwmon: driver names, labels and readings)",
            "Thermal zones (/sys/class/thermal)",
            "Processor model name (/proc/cpuinfo)",
        ],
        NeedsAdmin = false,
        // A temperature taken while other checks keep the processor busy measures the scan.
        RunsAlone = true,
        EstimatedDuration = TimeSpan.FromSeconds(3),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        // Peaks per reading, over a few samples: one instant can land between two bursts of work.
        var peaks = new Dictionary<(string MetricId, string Subject), (string Label, PickedTemperature Reading)>();
        void Keep(string metricId, string label, PickedTemperature reading)
        {
            var key = (metricId, reading.Subject);
            if (!peaks.TryGetValue(key, out var seen) || reading.Celsius > seen.Reading.Celsius)
                peaks[key] = (label, reading);
        }

        var anySource = false;
        for (var sample = 0; sample < samples; sample++)
        {
            if (sample > 0) await Task.Delay(interval, ct).ConfigureAwait(false);
            var chips = ReadChips(out var hwmonError);
            var zones = ReadZones(out var thermalError);
            anySource |= hwmonError is null || thermalError is null;
            if (sample == 0 && hwmonError is not null && thermalError is not null)
            {
                failures.Record("Temperature sensors", $"{hwmonError}; {thermalError}");
                return failures.Resolve(gotAnything: false);
            }

            if (Temperatures.ProcessorSensor(chips, zones) is { } cpu) Keep("cpu.temperature_c", "CPU temperature", cpu);
            else if (Temperatures.AcpiZone(zones) is { } zone) Keep("cpu.zone_temperature_c", "Temperature near the processor", zone);
            foreach (var drive in Temperatures.NvmeDrives(chips)) Keep("storage.temperature_c", "Drive temperature", drive);
            foreach (var (metricId, label, reading) in Temperatures.Graphics(chips)) Keep(metricId, label, reading);
        }

        // A processor sensor seen in any sample wins over a zone seen in another.
        if (peaks.Keys.Any(k => k.MetricId == "cpu.temperature_c"))
            foreach (var key in peaks.Keys.Where(k => k.MetricId == "cpu.zone_temperature_c").ToList()) peaks.Remove(key);

        var cpuName = files.TryRead("/proc/cpuinfo") is { } cpuinfo ? ProcFiles.CpuModelName(cpuinfo) : null;
        var readings = samples == 1 ? "one reading" : $"highest of {samples} readings {interval.TotalSeconds:0.#} s apart";
        foreach (var ((metricId, _), (label, reading)) in peaks)
        {
            var (category, subject, detail) = metricId switch
            {
                "cpu.temperature_c" => (HealthCategory.Sensors, cpuName ?? reading.Subject,
                    $"The processor's own sensor ({reading.Source}), {readings}. Modern processors are designed to touch " +
                    "their limit briefly under heavy load, so a high peak alone is not a fault; sustained heat with little running is."),
                "cpu.zone_temperature_c" => (HealthCategory.Sensors, reading.Subject,
                    $"The firmware's thermal-zone reading near the processor ({reading.Source}), {readings}. The processor's " +
                    "own sensor was not found; this one can read a few degrees differently."),
                "storage.temperature_c" => (HealthCategory.Storage, reading.Subject,
                    $"The drive's own sensor ({reading.Source}), {readings}."),
                _ => (HealthCategory.Sensors, reading.Subject, $"{reading.Source}, {readings}."),
            };
            metrics.Add(new Metric(metricId, label, Math.Round(reading.Celsius), category, Unit: "°C", Subject: subject, Detail: detail));
        }

        if (!peaks.Keys.Any(k => k.MetricId is "cpu.temperature_c" or "cpu.zone_temperature_c"))
            metrics.Add(new Metric("sensors.cpu_temperature_read", "Processor temperature read", false, HealthCategory.Sensors,
                InfoOnly: true,
                Detail: "No processor temperature could be read on this PC (no coretemp, k10temp or x86_pkg_temp sensor, and no " +
                        "plausible ACPI zone; normal in a virtual machine), so the Sensors grade does not cover the processor."));

        return failures.Resolve(gotAnything: anySource);
    }

    private List<HwmonChip> ReadChips(out string? error)
    {
        var chips = new List<HwmonChip>();
        foreach (var dir in files.TryListDirectories("/sys/class/hwmon", out error) ?? [])
        {
            var path = $"/sys/class/hwmon/{dir}";
            var name = files.TryRead($"{path}/name")?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var temperatures = new List<HwmonTemperature>();
            foreach (var input in files.ListFiles(path, "temp*_input"))
            {
                if (Temperatures.FromMilli(files.TryRead($"{path}/{input}")) is not { } celsius) continue;
                var label = files.TryRead($"{path}/{input[..^"_input".Length]}_label")?.Trim();
                temperatures.Add(new HwmonTemperature(string.IsNullOrEmpty(label) ? null : label, celsius));
            }
            chips.Add(new HwmonChip(dir, name, files.LinkTargetName($"{path}/device"), temperatures));
        }
        return chips;
    }

    private List<ThermalZone> ReadZones(out string? error)
    {
        var zones = new List<ThermalZone>();
        foreach (var dir in files.TryListDirectories("/sys/class/thermal", out error) ?? [])
        {
            if (!dir.StartsWith("thermal_zone", StringComparison.Ordinal)) continue;
            var type = files.TryRead($"/sys/class/thermal/{dir}/type")?.Trim();
            if (string.IsNullOrEmpty(type) || Temperatures.FromMilli(files.TryRead($"/sys/class/thermal/{dir}/temp")) is not { } celsius)
                continue;
            zones.Add(new ThermalZone(dir, type, celsius));
        }
        return zones;
    }
}
