using PcHealth.Modules.Windows.Sensors;
using Xunit;

namespace PcHealth.Tests;

public class SensorAnalysisTests
{
    private static SensorReading Reading(string name, double peak) => new("Hardware", name, peak, peak);

    [Fact]
    public void Package_temperature_prefers_the_vendor_package_sensor_over_a_hotter_core()
    {
        var readings = new[]
        {
            Reading("Core #1", 72),
            Reading("CPU Package", 65),
            Reading("Core #4", 71),
        };

        var selected = SensorAnalysis.SelectPackageTemperature(readings);
        Assert.Equal("CPU Package", selected!.Name);
    }

    [Fact]
    public void Package_temperature_understands_the_amd_sensor_name()
    {
        var readings = new[] { Reading("Core #1", 50), Reading("Core (Tctl/Tdie)", 61) };
        Assert.Equal("Core (Tctl/Tdie)", SensorAnalysis.SelectPackageTemperature(readings)!.Name);
    }

    [Fact]
    public void Package_temperature_falls_back_to_the_hottest_core()
    {
        var readings = new[] { Reading("Core #1", 55), Reading("Core #2", 68), Reading("Core #3", 60) };
        Assert.Equal("Core #2", SensorAnalysis.SelectPackageTemperature(readings)!.Name);
    }

    [Fact]
    public void Package_temperature_is_absent_when_nothing_was_readable()
    {
        Assert.Null(SensorAnalysis.SelectPackageTemperature([]));
    }

    [Fact]
    public void Stopped_fans_are_only_a_problem_when_the_device_is_genuinely_hot()
    {
        var stopped = new[] { Reading("CPU Fan", 0), Reading("Case Fan", 0) };

        // Idle PCs routinely park their fans; that is a feature, not a fault.
        Assert.False(SensorAnalysis.IsStoppedFanConcerning(stopped, 41, SensorAnalysis.CpuFansExpectedAboveC));
        Assert.True(SensorAnalysis.IsStoppedFanConcerning(stopped, 78, SensorAnalysis.CpuFansExpectedAboveC));
    }

    [Fact]
    public void An_idle_graphics_card_with_stopped_fans_is_not_flagged()
    {
        // Regression: an RTX 3060 sitting at 62 C core with its zero-RPM idle mode engaged
        // was reported as an airflow fault, because the check used the 72 C hot-spot reading.
        var gpuTemperatures = new[] { Reading("GPU Core", 62), Reading("GPU Hot Spot", 72) };
        var stoppedFans = new[] { Reading("GPU Fan 1", 0), Reading("GPU Fan 2", 0) };

        var representative = SensorAnalysis.SelectRepresentativeTemperature(gpuTemperatures)!;
        Assert.Equal("GPU Core", representative.Name);
        Assert.False(SensorAnalysis.IsStoppedFanConcerning(stoppedFans, representative.Peak, SensorAnalysis.GpuFansExpectedAboveC));
    }

    [Fact]
    public void A_genuinely_hot_card_with_stopped_fans_is_flagged()
    {
        var gpuTemperatures = new[] { Reading("GPU Core", 86), Reading("GPU Hot Spot", 99) };
        var stoppedFans = new[] { Reading("GPU Fan 1", 0) };

        var representative = SensorAnalysis.SelectRepresentativeTemperature(gpuTemperatures)!;
        Assert.True(SensorAnalysis.IsStoppedFanConcerning(stoppedFans, representative.Peak, SensorAnalysis.GpuFansExpectedAboveC));
    }

    [Theory]
    [InlineData("GPU Hot Spot")]
    [InlineData("GPU Memory Junction")]
    [InlineData("VRM MOS")]
    public void Hot_by_design_sensors_never_represent_a_device(string sensorName)
    {
        Assert.True(SensorAnalysis.IsHotByDesignSensor(sensorName));
        var readings = new[] { Reading("GPU Core", 55), Reading(sensorName, 95) };
        Assert.Equal("GPU Core", SensorAnalysis.SelectRepresentativeTemperature(readings)!.Name);
    }

    [Fact]
    public void A_device_with_only_hot_by_design_sensors_has_no_representative_temperature()
    {
        Assert.Null(SensorAnalysis.SelectRepresentativeTemperature([Reading("GPU Hot Spot", 90)]));
    }

    [Fact]
    public void One_spinning_fan_is_enough_to_clear_the_check()
    {
        var mixed = new[] { Reading("CPU Fan", 900), Reading("Case Fan", 0) };
        Assert.False(SensorAnalysis.IsStoppedFanConcerning(mixed, 85, SensorAnalysis.CpuFansExpectedAboveC));
    }

    [Fact]
    public void No_fan_sensors_at_all_is_not_reported_as_a_fault()
    {
        // Plenty of laptops and small PCs expose no fan sensors; absence is not evidence.
        Assert.False(SensorAnalysis.IsStoppedFanConcerning([], 90, SensorAnalysis.CpuFansExpectedAboveC));
    }
}
