using PcHealth.Core.Metrics;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows.Benchmarks;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// "Processor speed while busy": the clock the processor actually ran at while the CPU benchmark
/// kept one core busy. The T470p this was built for ran builds at 67-84% of its base clock
/// with dried-out cooling (HW-001), which no idle reading could show.
/// </summary>
public sealed class CpuSlowdownTests
{
    private static readonly VerdictEngine Engine = new(RulesLoader.LoadEmbedded());

    [Fact]
    public void The_first_sample_is_dropped_and_the_median_reported() =>
        // 0 is the counter's first, uncooked read; the rest median to 71.
        Assert.Equal(71, ClockUnderLoad.Typical([0, 70, 72, 68, 75, 71]));

    [Fact]
    public void Impossible_values_are_ignored_rather_than_averaged_in() =>
        Assert.Equal(130, ClockUnderLoad.Typical([130, 128, 131, 0, 9999, 130, 129, 132]));

    [Fact]
    public void Too_few_samples_report_nothing_instead_of_a_guess() =>
        Assert.Null(ClockUnderLoad.Typical([90, 91, 92, 93, 94]));   // 4 usable after the first

    [Theory]
    [InlineData(135, Grade.Good)]    // turbo with one core busy: healthy
    [InlineData(92, Grade.Normal)]   // holding back a little
    [InlineData(72, Grade.Bad)]      // the T470p's range under load
    public void Speed_while_busy_is_graded(double percentOfBase, Grade expected) =>
        Assert.Equal(expected, Engine.EvaluateOne(
            new Metric("bench.cpu.clock_pct_of_base", "clock", percentOfBase, HealthCategory.Performance)).Grade);

    [Fact]
    public void Firmware_speed_limit_events_are_shown_but_never_graded() =>
        // Laptops log a few at start-up while healthy; without a baseline a grade would cry wolf.
        Assert.Equal(Grade.Unknown, Engine.EvaluateOne(
            new Metric("events.cpu_speed_limited_30d", "limits", 12, HealthCategory.Performance, InfoOnly: true)).Grade);
}
