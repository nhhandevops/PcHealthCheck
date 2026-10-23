using System.Xml;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// How much of its recent use a laptop spent on battery, from the battery report's daily
/// history (ActiveAcTime / ActiveDcTime, ISO-8601 durations, locale-proof). A worn battery on
/// a laptop that never leaves its charger limits mobility; it does not put anything at risk.
/// The T470p audited on 2026-10-21 was graded "Action needed" at 50% capacity while it had
/// spent 0 seconds on battery in the whole report window (HW-001 follow-up).
/// </summary>
public static class BatteryUsage
{
    public sealed record Share(double OnBatteryPct, double HoursInUse);

    public static Share? Summarize(IEnumerable<(string? Ac, string? Dc)> entries)
    {
        var ac = TimeSpan.Zero;
        var dc = TimeSpan.Zero;
        foreach (var (acText, dcText) in entries)
        {
            if (Duration(acText) is { } a) ac += a;
            if (Duration(dcText) is { } d) dc += d;
        }
        var total = ac + dc;
        if (total <= TimeSpan.Zero) return null;
        return new Share(Math.Round(100.0 * dc.Ticks / total.Ticks, 1), Math.Round(total.TotalHours, 1));
    }

    private static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var value = XmlConvert.ToTimeSpan(text);
            return value >= TimeSpan.Zero ? value : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The battery's charge right now, in words. Not graded: a laptop on mains power that is not
/// charging is usually holding at a limit on purpose (most stop at 95-100%, many offer an 80%
/// limit, Lenovo's conservation mode holds 55-60%), so "not charging" is only a fault when no
/// such limit is set, which Windows cannot tell us.
/// </summary>
public static class BatteryChargeState
{
    public static string Describe(double percent, bool onMains, bool charging, bool discharging) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Round(percent):0}% charged, ") +
        (onMains
            ? charging ? "on mains power, charging" : "on mains power, not charging"
            : discharging ? "running on battery" : "on battery power");
}
