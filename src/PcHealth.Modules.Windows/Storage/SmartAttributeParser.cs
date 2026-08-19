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

    public static string NameFor(int id) => id switch
    {
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
