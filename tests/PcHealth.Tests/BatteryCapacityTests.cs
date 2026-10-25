using PcHealth.Core.Metrics;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Locks the guard added after the first fleet audit, where a nine-year-old ThinkPad reported a
/// full charge of 25,800 mWh against a 23,510 mWh design — 109.7% — and the app graded it Good.
/// The manager's table then said that laptop's battery was better than new.
///
/// Both directions are tested. Over-correcting would be the worse bug: a fresh pack routinely
/// measures a shade over 100%, and refusing to grade those would fire the "some checks could not
/// finish" banner on healthy laptops until nobody read it any more.
/// </summary>
public class BatteryCapacityTests
{
    [Theory]
    [InlineData(100)]   // exactly as designed
    [InlineData(102.5)] // a fresh pack, routinely a shade over
    [InlineData(105)]   // the edge of the margin, still believed
    [InlineData(77.5)]  // an ordinary worn pack
    [InlineData(4.8)]   // a dead pack: a real measurement, and a Bad one
    [InlineData(0.1)]
    public void Readings_a_battery_could_hold_are_graded(double pct)
        => Assert.True(BatteryCapacity.IsPlausible(pct));

    [Theory]
    [InlineData(105.1)]
    [InlineData(109.7)] // the reading from the audit
    [InlineData(250)]
    [InlineData(10000)]
    public void Readings_a_battery_could_not_hold_are_not_graded(double pct)
        => Assert.False(BatteryCapacity.IsPlausible(pct));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_missing_or_negative_reading_is_not_a_measurement(double pct)
        => Assert.False(BatteryCapacity.IsPlausible(pct));

    [Fact]
    public void The_advice_names_the_figure_and_says_it_is_not_graded()
    {
        var advice = BatteryCapacity.CalibrationAdvice(109.7);

        Assert.Contains("109.7%", advice, StringComparison.Ordinal);
        Assert.Contains("not measure wear", advice, StringComparison.Ordinal);
        Assert.Contains("ungraded", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_says_wear_was_not_established_so_the_scan_degrades()
        => Assert.Contains("NOT established", BatteryCapacity.UnreliableNote, StringComparison.Ordinal);

    /// <summary>
    /// The margin is a judgement, not an accident: it has to clear a new pack's overshoot and
    /// still catch a gauge that has lost calibration. If someone moves it, they should have to
    /// mean it.
    /// </summary>
    [Fact]
    public void The_plausible_margin_is_the_one_that_was_reasoned_about()
        => Assert.Equal(105, BatteryCapacity.PlausibleMaxPct);
}
