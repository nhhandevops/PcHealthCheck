using LibreHardwareMonitor.Hardware;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Sensors;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// CPU/GPU temperatures, fan speeds and memory use, read through LibreHardwareMonitor.
///
/// This module declares LoadsDriver because processor and motherboard sensors are read
/// through a kernel-level driver (PawnIO, installed separately by the user — this app
/// never installs one). Graphics and memory need no driver. Everything is a read: it
/// never sets a fan curve, voltage, or clock.
/// </summary>
internal sealed class SensorsModule : WindowsModuleBase
{
    private const int SampleCount = 5;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(700);

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "sensors",
        Name = "Temperatures & fans",
        Description = "CPU and GPU temperatures, fan speeds and memory use. Graphics and memory need no driver; processor and motherboard sensors additionally use the PawnIO kernel driver if you have installed it. Reads only — it never changes fan curves, clocks or voltages.",
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

        var failures = new ModuleFailures(notes);
        var peaks = new Dictionary<(HardwareType Hardware, SensorType Type, string Name), SensorReading>();
        var detectedHardwareNote = "";
        var cpuTemperatureOffered = false;
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

            // Whether a processor temperature sensor exists at all, captured here because Close()
            // empties the hardware list. This, and not a registry key, is what separates "the read
            // was attempted and gave nothing back" from "there was never anything here to read":
            // a virtual machine exposes no CPU temperature sensor whatever driver is installed.
            cpuTemperatureOffered = computer.Hardware.Any(h => h.HardwareType == HardwareType.Cpu
                && h.Sensors.Any(sensor => sensor.SensorType == SensorType.Temperature));
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

        BuildMetrics(peaks, metrics, failures, detectedHardwareNote, cpuTemperatureOffered);

        // Reading some sensors is not the same as reading the ones this section is named for: a
        // report with no CPU temperature must not arrive as a finished check. Resolve keeps the
        // one distinction that matters — a driver the user chose not to install leaves this
        // Completed, a reading that was attempted and came back with nothing does not. Sampling
        // can also end with readings that produce no metric at all (a peak that is only a CPU load
        // figure, say), and a failure with nothing gathered is Failed, not a partial result.
        return failures.Resolve(gotAnything: metrics.Count > 0);
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
        List<Metric> metrics, ModuleFailures failures, string detectedHardwareNote,
        bool cpuTemperatureOffered)
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
        else if (Ring0Driver.IsPawnIoRunning())
        {
            // The driver this reading depends on is loaded and running, and still nothing came
            // back. That is a read that was attempted and failed.
            //
            // The gate is the driver's RUNNING state, not the presence of a temperature sensor.
            // LibreHardwareMonitor activates a processor's temperature sensors from CPUID before
            // any driver is involved and only their values need ring0, so "a sensor was offered
            // and read back empty" is what every ordinary PC without PawnIO looks like — measured
            // on an i7-7820HQ: eleven CPU temperature sensors, every value null. Degrading on that
            // would raise the amber "some checks could not finish" banner on the majority of
            // machines, where nothing is wrong and nothing was missed.
            failures.Record("CPU temperature",
                "PawnIO is running but the processor temperature came back empty on every sample — " +
                $"this chipset may not be supported. {detectedHardwareNote}");
        }
        else if (Ring0Driver.IsPawnIoInstalled())
        {
            // Registered but not running: nothing was attempted, so nothing failed. This is also
            // what a virtual machine looks like, where no virtual CPU exposes a temperature for
            // any driver to read. Name what to check and leave the status alone.
            failures.Note(
                "Processor temperature is unavailable. PawnIO is installed on this PC but its driver " +
                "is not running, so either this is a virtual machine — no virtual CPU exposes a " +
                "temperature — or the driver was blocked or removed: check the PawnIO service, or " +
                $"reinstall it from pawnio.eu. {detectedHardwareNote}");
        }
        else
        {
            // Not having PawnIO is the user's decision, not a fault: this app never installs a
            // kernel driver, and graphics and memory readings still work without one. Degrading
            // here would raise "some checks could not finish" on every scan on every PC without
            // the driver — which is most of them — and a banner that always shows is one nobody
            // reads, including on the scan where something really did break.
            failures.Note(Ring0Driver.MissingDriverAdvice);
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
