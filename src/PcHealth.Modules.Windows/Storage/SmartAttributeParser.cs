namespace PcHealth.Modules.Windows.Storage;

public sealed record SmartAttribute(int Id, string Name, int Current, int Worst, long Raw)
{
    /// <summary>Raw value with the vendor padding stripped for the attributes where the
    /// convention is well established (temperature and hour counters pack extra data
    /// into the upper raw bytes).</summary>
    public long MeaningfulRaw => Id switch
    {
        194 or 190 => Raw & 0xFF,          // temperature: current value in the low byte
        9 => Raw & 0xFFFFFFFF,             // power-on hours
        188 => Raw & 0xFFFF,               // command timeout: low 16 bits
        _ => Raw,
    };
}

/// <summary>
/// Parses the 512-byte ATA "SMART READ DATA" structure that Windows exposes through
/// WMI (MSStorageDriver_FailurePredictData.VendorSpecific): a 2-byte revision followed
/// by up to 30 twelve-byte attribute entries.
/// </summary>
public static class SmartAttributeParser
{
    private const int AttributeTableOffset = 2;
    private const int EntrySize = 12;
    private const int MaxEntries = 30;

    public static IReadOnlyList<SmartAttribute> Parse(byte[]? vendorSpecific)
    {
        if (vendorSpecific is null || vendorSpecific.Length < AttributeTableOffset + EntrySize) return [];

        var attributes = new List<SmartAttribute>(MaxEntries);
        for (var i = 0; i < MaxEntries; i++)
        {
            var offset = AttributeTableOffset + (i * EntrySize);
            if (offset + EntrySize > vendorSpecific.Length) break;

            var id = vendorSpecific[offset];
            if (id == 0) continue; // empty slot

            var current = vendorSpecific[offset + 3];
            var worst = vendorSpecific[offset + 4];

            long raw = 0;
            for (var b = 0; b < 6; b++)
            {
                raw |= (long)vendorSpecific[offset + 5 + b] << (8 * b); // little-endian
            }

            attributes.Add(new SmartAttribute(id, NameFor(id), current, worst, raw));
        }
        return attributes;
    }

    /// <summary>
    /// Parses MSStorageDriver_FailurePredictThresholds.VendorSpecific: the same 2-byte revision and
    /// twelve-byte entries as the attribute table, each holding [id, threshold, ...]. A threshold of
    /// 0 means the attribute can never fail.
    /// </summary>
    public static IReadOnlyDictionary<int, int> ParseThresholds(byte[]? vendorSpecific)
    {
        var thresholds = new Dictionary<int, int>();
        if (vendorSpecific is null) return thresholds;
        for (var i = 0; i < MaxEntries; i++)
        {
            var offset = AttributeTableOffset + (i * EntrySize);
            if (offset + EntrySize > vendorSpecific.Length) break;
            var id = vendorSpecific[offset];
            if (id != 0) thresholds[id] = vendorSpecific[offset + 1];
        }
        return thresholds;
    }

    /// <summary>
    /// Attributes the drive's own firmware rates as failed: normalized value at or below the
    /// threshold the vendor set for it, now or at its worst. This is SMART's one judgement that
    /// means the same on every brand, which is why the app grades it and does NOT interpret
    /// attributes such as 171/172 (program/erase failures on Intel and SandForce), 183 (link
    /// downshifts on Intel, runtime bad blocks on Samsung) or 241 (data written, in units that
    /// differ by vendor): read by number on the wrong brand, those would raise false alarms.
    /// </summary>
    public static (IReadOnlyList<SmartAttribute> Now, IReadOnlyList<SmartAttribute> InThePast) AtThreshold(
        IReadOnlyList<SmartAttribute> attributes, IReadOnlyDictionary<int, int> thresholds)
    {
        bool Has(SmartAttribute a, out int t) => thresholds.TryGetValue(a.Id, out t) && t > 0;
        var now = attributes.Where(a => Has(a, out var t) && a.Current <= t).ToList();
        var past = attributes.Where(a => Has(a, out var t) && a.Current > t && a.Worst <= t).ToList();
        return (now, past);
    }

    public static string NameFor(int id) => id switch
    {
        170 => "Available reserved space",
        1 => "Raw read error rate",
        3 => "Spin-up time",
        4 => "Start/stop count",
        5 => "Reallocated sectors",
        7 => "Seek error rate",
        9 => "Power-on hours",
        10 => "Spin retry count",
        12 => "Power cycle count",
        177 => "Wear leveling count",
        179 => "Used reserved block count",
        181 => "Program fail count",
        182 => "Erase fail count",
        187 => "Reported uncorrectable errors",
        188 => "Command timeouts",
        190 => "Airflow temperature",
        193 => "Load/unload cycle count",
        194 => "Temperature",
        196 => "Reallocation event count",
        197 => "Current pending sectors",
        198 => "Offline uncorrectable sectors",
        199 => "Interface CRC errors",
        202 => "Percent lifetime remaining",
        231 => "SSD life left",
        233 => "Media wearout indicator",
        241 => "Total data written",
        242 => "Total data read",
        _ => $"Attribute {id}",
    };

    /// <summary>
    /// Vendor attributes whose NORMALIZED value expresses "percent of endurance left"
    /// on SATA SSDs. Percent consumed is therefore 100 - value.
    /// </summary>
    public static readonly int[] SsdLifeLeftAttributes = [231, 233, 177, 202];
}
