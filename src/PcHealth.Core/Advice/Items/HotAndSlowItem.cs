namespace PcHealth.Core.Advice.Items;

/// <summary>
/// O3: this PC runs hot and slows itself down (REMEDIES-PLAN O3, STEP-B-SPEC 5). It needs both: a
/// processor temperature graded Normal or Bad, 80 °C and up, and the processor speed test graded
/// Normal or Bad, under its base clock with one core busy. A hot processor that still runs fast,
/// or a slow one that is cool, is not this.
///
/// Windows' own "firmware held the processor back" count is information only, with no band from
/// healthy PCs yet (principle 14), so it is quoted and never triggers. A clock read on battery is
/// information only too. The kit never runs the speed test, so it gets at most the grey note.
///
/// The charger, lap and laptop-maker lines are for a PC with a battery reading in this report.
/// Without one it may be a desktop, which the speed test counts as on mains, so the card is worded
/// for any PC; a laptop whose battery was not read loses only the charger line.
/// </summary>
public sealed class HotAndSlowItem : IAdviceItem
{
    public const string ItemId = "O3";
    public const string SensorsModule = "sensors", BenchmarkModule = "bench.cpu";
    public const string ClockId = "bench.cpu.clock_pct_of_base";

    /// <summary>The processor's own temperature, or the thermal zone near it when that is all Windows gives.</summary>
    public static readonly IReadOnlyList<string> TemperatureIds = ["cpu.temperature_c", "cpu.zone_temperature_c"];

    private const string FirmwareLimited = "events.cpu_speed_limited_30d";

    /// <summary>The battery check's readings: written only where Windows lists a battery.</summary>
    private const string BatteryPrefix = "battery.";

    public string Id => ItemId;

    public string Area => "heat";

    public string Os => "windows";

    public int Rank => 25;

    public IReadOnlyList<string> Needs { get; } = [SensorsModule, BenchmarkModule];

    public IReadOnlyList<Recommendation> Evaluate(AdviceFacts facts, IReadOnlyList<Recommendation> earlier)
    {
        var hot = TemperatureIds.SelectMany(id => facts.Problems(id)).Where(v => v.Metric.Value.Number is not null).ToList();
        var slow = facts.Problems(ClockId).Where(v => v.Metric.Value.Number is not null).ToList();
        if (hot.Count == 0 || slow.Count == 0) return [];

        var card = new AdviceCard(ItemId, ItemId, Area, AdviceUrgency.Advice, Rank).Title("advice.o3.title");
        foreach (var reading in hot.Concat(slow)) card.Because(reading);

        // The temperature was read before the speed test ran (the sensors check runs first), so the
        // sentence says "earlier in this scan", not "under load".
        card.Line(AdviceLineRole.Finding, "advice.o3.finding",
            ("clockPct", AdviceArg.Percent(slow.Min(v => v.Metric.Value.Number!.Value))),
            ("tempC", AdviceArg.Celsius(hot.Max(v => v.Metric.Value.Number!.Value))));
        if (facts.First(FirmwareLimited)?.Metric.Value.Number is { } times && times > 0)
            card.Line(AdviceLineRole.Finding, "advice.o3.firmware-limit", ("count", AdviceArg.Count(times)));

        if (facts.Verdicts.Any(v => v.Metric.Id.StartsWith(BatteryPrefix, StringComparison.Ordinal)))
        {
            card.Line(AdviceLineRole.Why, "advice.o3.why")
                .Line(AdviceLineRole.Do, "advice.o3.do.vents")
                .Line(AdviceLineRole.Do, "advice.o3.do.charger")
                .Line(AdviceLineRole.ForIt, "advice.o3.for-it.clean")
                .Line(AdviceLineRole.ForIt, "advice.o3.for-it.bios");
        }
        else
        {
            card.Line(AdviceLineRole.Why, "advice.o3.why.pc")
                .Line(AdviceLineRole.Do, "advice.o3.do.vents.pc")
                .Line(AdviceLineRole.ForIt, "advice.o3.for-it.clean")
                .Line(AdviceLineRole.ForIt, "advice.o3.for-it.bios.pc");
        }
        return [card.Line(AdviceLineRole.Finding, "advice.o3.next-scan").Build()];
    }
}
