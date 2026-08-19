using LibreHardwareMonitor.Hardware;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Sensors;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// CPU/GPU temperatures, fan speeds and memory use, read through LibreHardwareMonitor.
///
/// This is the one module that loads a kernel-level driver, which is why it declares
/// LoadsDriver and is confirmed separately: reading motherboard and CPU sensors is not
/// possible from user mode. Everything it does is still a read — it never sets a fan
/// curve, voltage, or clock.
/// </summary>
internal sealed class SensorsModule : WindowsModuleBase
{
    private const int SampleCount = 5;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(700);

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "sensors",
        Name = "Temperatures & fans",
        Description = "CPU and GPU temperatures, fan speeds and memory use. Loads a kernel-level driver, because motherboard sensors cannot be read any other way. Reads only — it never changes fan curves, clocks or voltages.",
        Reads = ["CPU/GPU/motherboard sensors (kernel driver)", "Memory usage"],
        NeedsAdmin = true,
        LoadsDriver = true,
        EstimatedDuration = TimeSpan.FromSeconds(8),
    };

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        if (!Elevation.IsElevated())
        {
            notes.Add("Sensor access requires administrator rights.");
            return ModuleStatus.Failed;
        }

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = false,      // covered by the drive health modules
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
            IsBatteryEnabled = false,
        };

        var peaks = new Dictionary<(HardwareType Hardware, SensorType Type, string Name), SensorReading>();
        var detectedHardwareNote = "";
        try
        {
            computer.Open();

            // Record what the sensor library could actually see. When a chipset or the
            // driver refuses, the difference between "no CPU object at all" and "a CPU
            // object with no temperature sensors" is the whole diagnosis.
            var detected = computer.Hardware
                .Select(h => $"{h.HardwareType} ({h.Sensors.Length} sensors)")
                .ToList();
            detectedHardwareNote = detected.Count > 0
                ? "Sensor library detected: " + string.Join(", ", detected) + "."
                : "The sensor library detected no hardware at all, which usually means its kernel driver did not load.";

            var visitor = new UpdateVisitor();
            for (var i = 0; i < SampleCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ModuleProgress($"sampling sensors ({i + 1}/{SampleCount})"));
                computer.Accept(visitor);
                Collect(computer.Hardware, peaks);
                if (i < SampleCount - 1) await Task.Delay(SampleInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // A missing/blocked driver, or an unsupported chipset, must not sink the scan.
            notes.Add($"Sensor library could not start: {e.GetType().Name}: {e.Message}");
            return ModuleStatus.Failed;
        }
        finally
        {
            try { computer.Close(); } catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }
        }

        if (peaks.Count == 0)
        {
            notes.Add("No sensors were readable on this hardware. " + detectedHardwareNote);
            return ModuleStatus.PartiallyCompleted;
        }

        BuildMetrics(peaks, metrics, notes, detectedHardwareNote);
        return ModuleStatus.Completed;
    }

    private static void Collect(
        IEnumerable<IHardware> hardware,
        Dictionary<(HardwareType, SensorType, string), SensorReading> peaks)
    {
        foreach (var device in hardware)
        {
            foreach (var sensor in device.Sensors)
            {
                if (sensor.Value is not { } value || double.IsNaN(value)) continue;
                if (sensor.SensorType is not (SensorType.Temperature or SensorType.Fan or SensorType.Load)) continue;

                var key = (device.HardwareType, sensor.SensorType, sensor.Name);
                var reading = peaks.TryGetValue(key, out var existing)
                    ? existing with { Peak = Math.Max(existing.Peak, value), Latest = value }
                    : new SensorReading(device.Name, sensor.Name, value, value);
                peaks[key] = reading;
            }
            Collect(device.SubHardware, peaks);
        }
    }

    private static void BuildMetrics(
        Dictionary<(HardwareType Hardware, SensorType Type, string Name), SensorReading> peaks,
        List<Metric> metrics, List<string> notes, string detectedHardwareNote)
    {
        List<SensorReading> Readings(SensorType type, params HardwareType[] hardware) =>
            [.. peaks.Where(p => p.Key.Type == type && hardware.Contains(p.Key.Hardware)).Select(p => p.Value)];

        var cpuTemperatures = Readings(SensorType.Temperature, HardwareType.Cpu);
        var gpuTypes = new[] { HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel };
        var gpuTemperatures = Readings(SensorType.Temperature, gpuTypes);
        var fans = Readings(SensorType.Fan, HardwareType.Motherboard, HardwareType.SuperIO);
        fans.AddRange(Readings(SensorType.Fan, gpuTypes));

        if (SensorAnalysis.SelectPackageTemperature(cpuTemperatures) is { } cpu)
        {
            metrics.Add(new Metric("cpu.temperature_c", "CPU temperature", Math.Round(cpu.Peak), HealthCategory.Sensors,
                Unit: "°C", Subject: cpu.Hardware,
                Detail: $"Highest reading over a few seconds ({cpu.Name}). Modern CPUs are designed to touch their " +
                        "limit briefly under heavy load, so a high peak alone is not a fault — sustained heat with " +
                        "reduced clock speeds is."));
        }
        else
        {
            notes.Add("No CPU temperature was readable. This normally means the sensor driver could not " +
                      "attach to this chipset — a security product blocking driver loading is the usual cause. " +
                      detectedHardwareNote);
        }

        AddGpuTemperature(gpuTemperatures, "GPU Core", "gpu.temperature_c", "GPU temperature", metrics);
        AddGpuTemperature(gpuTemperatures, "GPU Hot Spot", "gpu.hotspot_c", "GPU hot spot", metrics);
        AddGpuTemperature(gpuTemperatures, "GPU Memory Junction", "gpu.memory_junction_c", "GPU memory temperature", metrics);

        var spinning = fans.Where(f => f.Peak > 0).ToList();
        if (fans.Count > 0)
        {
            metrics.Add(new Metric("fans.spinning_count", "Fans reporting a speed", spinning.Count,
                HealthCategory.Sensors, InfoOnly: true,
                Detail: spinning.Count > 0
                    ? "Speeds: " + string.Join(", ", spinning.Select(f => $"{f.Name} {f.Peak:0} RPM"))
                    : "All monitored fans report 0 RPM, which is normal when nothing is working hard — " +
                      "graphics cards and many PCs deliberately stop their fans when idle."));
        }

        // Airflow is judged PER DEVICE, against that device's own representative temperature.
        // A graphics card's hot spot always runs far above its core, and its fans are meant
        // to be stopped when idle, so comparing across devices or against hot-by-design
        // sensors reports a fault on a perfectly healthy machine.
        AddAirflowMetric(gpuTemperatures, Readings(SensorType.Fan, gpuTypes),
            SensorAnalysis.GpuFansExpectedAboveC, "graphics card", metrics);

        var cpuFans = Readings(SensorType.Fan, HardwareType.Motherboard, HardwareType.SuperIO);
        AddAirflowMetric(cpuTemperatures, cpuFans, SensorAnalysis.CpuFansExpectedAboveC, "processor", metrics);

        if (Readings(SensorType.Load, HardwareType.Memory).FirstOrDefault(r => r.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase)) is { } memory)
        {
            metrics.Add(new Metric("memory.used_pct", "Memory in use", Math.Round(memory.Latest),
                HealthCategory.Sensors, Unit: "%",
                Detail: "Measured while the scan ran, so it reflects what was open at the time."));
        }
    }

    private static void AddAirflowMetric(
        List<SensorReading> temperatures, List<SensorReading> fans,
        double expectedAboveC, string what, List<Metric> metrics)
    {
        if (fans.Count == 0) return;
        if (SensorAnalysis.SelectRepresentativeTemperature(temperatures) is not { } representative) return;

        var concerning = SensorAnalysis.IsStoppedFanConcerning(fans, representative.Peak, expectedAboveC);
        metrics.Add(new Metric("fans.airflow_ok", "Fans running when hot", !concerning,
            HealthCategory.Sensors, Subject: representative.Hardware,
            Detail: concerning
                ? $"The {what} reached {representative.Peak:0} °C ({representative.Name}) yet none of its fans reported any speed."
                : $"Fine: the {what} peaked at {representative.Peak:0} °C, and fans are either turning or not needed yet " +
                  $"(they are expected to run above {expectedAboveC:0} °C)."));
    }

    private static void AddGpuTemperature(
        List<SensorReading> gpuTemperatures, string sensorNamePrefix,
        string metricId, string label, List<Metric> metrics)
    {
        // Gate on the sensor actually existing: hot-spot and memory-junction sensors are
        // present on some cards and generations only, and inventing them would be worse
        // than omitting them.
        var reading = gpuTemperatures.FirstOrDefault(t =>
            t.Name.StartsWith(sensorNamePrefix, StringComparison.OrdinalIgnoreCase));
        if (reading is null) return;

        metrics.Add(new Metric(metricId, label, Math.Round(reading.Peak), HealthCategory.Sensors,
            Unit: "°C", Subject: reading.Hardware,
            Detail: $"Highest reading over a few seconds ({reading.Name})."));
    }
}
