using System.Globalization;

namespace PcHealth.Modules.Linux.Support;

/// <summary>One entry of /sys/class/power_supply: its name (BAT0, AC, hidpp_battery_0) and the attributes read.</summary>
internal sealed record PowerSupplyEntry(string Name, IReadOnlyDictionary<string, string> Attributes)
{
    public string? Get(string attribute) => Attributes.GetValueOrDefault(attribute);

    public long? GetNumber(string attribute) =>
        long.TryParse(Get(attribute), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>Full-charge against design capacity, in the unit the battery reports (Wh or mAh).</summary>
internal sealed record BatteryWear(double Pct, double Full, double Design, string Unit);

/// <summary>
/// Reading /sys/class/power_supply, which lists every power source the kernel knows: the
/// laptop's batteries, the charger, and also the batteries of a wireless mouse or keyboard.
/// Pure, so the cases a development laptop never shows (peripherals, empty slots, a battery
/// that reports charge instead of energy) are tested on fixture text.
/// </summary>
internal static class PowerSupply
{
    /// <summary>
    /// The only attributes the module opens. serial_number is deliberately absent: a battery's
    /// serial number identifies the machine and tells the reader nothing about its health.
    /// </summary>
    public static readonly string[] AttributesRead =
    [
        "type", "scope", "present", "online", "status", "capacity",
        "energy_full", "energy_full_design", "charge_full", "charge_full_design",
        "cycle_count", "manufacturer", "model_name", "technology", "charge_control_end_threshold",
    ];

    /// <summary>
    /// A battery that powers this computer. Peripherals' batteries say scope "Device"; the
    /// system's say "System" or, on most laptops, nothing at all.
    /// </summary>
    public static bool IsSystemBattery(PowerSupplyEntry e) =>
        string.Equals(e.Get("type"), "Battery", StringComparison.Ordinal) &&
        !string.Equals(e.Get("scope"), "Device", StringComparison.OrdinalIgnoreCase);

    public static bool IsPeripheralBattery(PowerSupplyEntry e) =>
        string.Equals(e.Get("type"), "Battery", StringComparison.Ordinal) &&
        string.Equals(e.Get("scope"), "Device", StringComparison.OrdinalIgnoreCase);

    /// <summary>"present" is 0 for an empty slot, such as a ThinkPad's removed second battery. Absent means present.</summary>
    public static bool IsPresent(PowerSupplyEntry e) => e.GetNumber("present") is not 0;

    /// <summary>
    /// Wear from energy_full / energy_full_design (µWh), or from the charge_* pair (µAh) on
    /// batteries that count charge instead. Null when either half is missing or zero, which some
    /// batteries do: that is "not known", never "healthy".
    /// </summary>
    public static BatteryWear? Wear(PowerSupplyEntry e)
    {
        if (e.GetNumber("energy_full") is > 0 and var full && e.GetNumber("energy_full_design") is > 0 and var design)
            return new BatteryWear(Math.Round(full * 100.0 / design, 1), full / 1e6, design / 1e6, "Wh");
        if (e.GetNumber("charge_full") is > 0 and var cFull && e.GetNumber("charge_full_design") is > 0 and var cDesign)
            return new BatteryWear(Math.Round(cFull * 100.0 / cDesign, 1), cFull / 1e3, cDesign / 1e3, "mAh");
        return null;
    }

    /// <summary>"SMP 01AV427 (Li-poly)": the maker, model and chemistry the battery's own chip reports.</summary>
    public static string? Identity(PowerSupplyEntry e)
    {
        var name = string.Join(' ', new[] { e.Get("manufacturer"), e.Get("model_name") }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        if (name.Length == 0) return null;
        var chemistry = e.Get("technology");
        return string.IsNullOrWhiteSpace(chemistry) || string.Equals(chemistry, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} ({chemistry})";
    }

    /// <summary>
    /// "80%, not charging; charge limit 80%". The kernel's status words are fixed English
    /// (Charging, Discharging, Full, Not charging, Unknown), not translated.
    /// </summary>
    public static string? ChargeState(PowerSupplyEntry e)
    {
        var level = e.GetNumber("capacity");
        var status = e.Get("status") switch
        {
            "Charging" => "charging",
            "Discharging" => "on battery",
            "Full" => "full",
            "Not charging" => "not charging",
            _ => null,
        };
        if (level is null && status is null) return null;
        var text = level is { } l && status is not null ? $"{l}%, {status}" : level is { } only ? $"{only}%" : status!;
        return e.GetNumber("charge_control_end_threshold") is > 0 and < 100 and var limit
            ? $"{text}; charge limit {limit}%"
            : text;
    }

    public static long? Cycles(PowerSupplyEntry e) => e.GetNumber("cycle_count") is > 0 and var c ? c : null;
}
