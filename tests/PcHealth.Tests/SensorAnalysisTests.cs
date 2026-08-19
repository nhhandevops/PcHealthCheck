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
    public void Stopped_fans_are_only_a_problem_when_something_is_hot()
    {
        var stopped = new[] { Reading("CPU Fan", 0), Reading("Case Fan", 0) };

        // Idle PCs routinely park their fans; that is a feature, not a fault.
        Assert.False(SensorAnalysis.IsStoppedFanConcerning(stopped, hottestTemperature: 41));
        Assert.True(SensorAnalysis.IsStoppedFanConcerning(stopped, hottestTemperature: 78));
    }

    [Fact]
    public void One_spinning_fan_is_enough_to_clear_the_check()
    {
        var mixed = new[] { Reading("CPU Fan", 900), Reading("Case Fan", 0) };
        Assert.False(SensorAnalysis.IsStoppedFanConcerning(mixed, hottestTemperature: 85));
    }

    [Fact]
    public void No_fan_sensors_at_all_is_not_reported_as_a_fault()
    {
        // Plenty of laptops and small PCs expose no fan sensors; absence is not evidence.
        Assert.False(SensorAnalysis.IsStoppedFanConcerning([], hottestTemperature: 90));
    }
}
