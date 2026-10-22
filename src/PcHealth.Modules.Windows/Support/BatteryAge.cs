namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// When the battery was made, and when its wear will reach a given point if it keeps fading at
/// the rate it has so far. With a manufacture date one scan is enough to say "about 13% a year,
/// 40% around July 2027"; without it, a dated projection needs weeks of trend history. The T470p
/// audit's most useful battery line came from exactly this (HW-001 follow-up).
///
/// The date comes from the SMBIOS "Portable Battery" structure (type 22), readable without admin
/// through WMI's MSSmBios_RawSMBiosTables. When the date-string index (offset 0x06) is 0, the date
/// is the packed SBDS word at offset 0x12: bits 15-9 year since 1980, 8-5 month, 4-0 day. A date
/// given as free text is skipped, because its format is not specified.
/// </summary>
public static class BatteryAge
{
    private const byte PortableBatteryType = 22;
    private const byte EndOfTableType = 127;

    public static IReadOnlyList<DateOnly> ManufactureDates(byte[]? smbios)
    {
        var dates = new List<DateOnly>();
        if (smbios is null) return dates;
        var i = 0;
        while (i + 4 <= smbios.Length)
        {
            int type = smbios[i], length = smbios[i + 1];
            if (length < 4 || i + length > smbios.Length) break;
            if (type == PortableBatteryType && length >= 0x14 && smbios[i + 0x06] == 0 &&
                FromSbdsWord(smbios[i + 0x12] | (smbios[i + 0x13] << 8)) is { } made)
            {
                dates.Add(made);
            }
            if (type == EndOfTableType) break;

            // Skip the formatted area, then the string set, which ends with two zero bytes.
            var j = i + length;
            while (j + 1 < smbios.Length && !(smbios[j] == 0 && smbios[j + 1] == 0)) j++;
            i = j + 2;
        }
        return dates;
    }

    public static DateOnly? FromSbdsWord(int word)
    {
        if (word == 0) return null;
        var year = 1980 + (word >> 9);
        var month = (word >> 5) & 0xF;
        var day = word & 0x1F;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }

    /// <summary>Capacity lost per year since manufacture, in % of design; null when too young to tell.</summary>
    public static double? LossPerYear(double capacityPct, DateOnly made, DateOnly today)
    {
        var years = (today.DayNumber - made.DayNumber) / 365.25;
        if (years < 0.5 || capacityPct is <= 0 or >= 100) return null;
        return (100 - capacityPct) / years;
    }

    /// <summary>
    /// The date capacity reaches <paramref name="targetPct"/> if loss stays linear, or null when
    /// it is already there, is not fading measurably, or would take more than 20 years.
    /// </summary>
    public static DateOnly? ReachesPercent(double capacityPct, DateOnly made, DateOnly today, double targetPct)
    {
        if (capacityPct <= targetPct || LossPerYear(capacityPct, made, today) is not { } perYear || perYear <= 0) return null;
        var yearsLeft = (capacityPct - targetPct) / perYear;
        return yearsLeft > 20 ? null : today.AddDays((int)Math.Round(yearsLeft * 365.25));
    }
}
