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

/// <summary>Why Windows refused the ATA SMART classes on this machine.</summary>
/// <remarks>
/// The point of having a type for this is that the answer is now looked up instead of assumed.
/// The note it replaces blamed missing administrator rights unconditionally, and on 2026-10-25
/// it said exactly that to the operator of an ELEVATED scan of an NVMe-only ThinkPad - sending
/// them after a UAC problem that did not exist, while that drive's health sat in the same report
/// under the wear counters (SMART-001).
/// </remarks>
public enum SmartUnavailableReason
{
    /// <summary>
    /// No disk in this PC sits on a bus that could ever expose an ATA SMART table. A fact about
    /// the hardware, not a check that broke.
    /// </summary>
    NoAtaDrivePresent,

    /// <summary>The scan has no administrator rights, which is one reason Windows refuses.</summary>
    NotElevated,

    /// <summary>
    /// A drive that should have been able to answer did not, on a scan that had the rights to
    /// ask - or the bus inventory could not be read, so nothing is known either way. The drive
    /// went unread and the module must say so.
    /// </summary>
    Unexplained,
}

/// <summary>
/// Works out what "the ATA SMART classes are not available" means on the machine in front of it,
/// from two facts that were available all along: whether the scan is elevated, and which buses
/// the physical disks sit on.
/// </summary>
public static class SmartAvailability
{
    // MSFT_PhysicalDisk.BusType, the same numbers DriveClassifier names.
    private const ushort Bus1394 = 4, BusUsb = 7, BusSd = 12, BusMmc = 13, BusNvme = 17;

    /// <summary>
    /// The buses whose drives are KNOWN to have no ATA SMART attribute table.
    /// </summary>
    /// <remarks>
    /// This list names the INCAPABLE side deliberately, and the direction is the whole safety
    /// property. A bus number this code has never met, and a disk whose bus Windows did not
    /// report, both fall OUTSIDE this list and therefore count as drives that should have
    /// answered - which keeps the check Failed. Written as a list of capable buses instead, one
    /// unfamiliar bus number would turn an unread SATA disk into "nothing to read here": the
    /// zero-presented-as-an-all-clear bug this project has already shipped four times.
    /// </remarks>
    private static readonly ushort[] NoAtaSmartBusTypes = [Bus1394, BusUsb, BusSd, BusMmc, BusNvme];

    /// <summary>Whether a disk on this bus could have answered. An unknown bus counts as yes.</summary>
    public static bool BusCouldReportAtaSmart(ushort? busType) =>
        busType is null || Array.IndexOf(NoAtaSmartBusTypes, busType.Value) < 0;

    /// <summary>
    /// <paramref name="busInventoryRead"/> is false when MSFT_PhysicalDisk itself could not be
    /// queried: the machine's buses are then unknown and the refusal stays unexplained. An empty
    /// inventory is treated the same way, because "Windows listed no disks at all" is not
    /// evidence that this PC holds no ATA drive.
    /// </summary>
    public static SmartUnavailableReason Diagnose(
        bool isElevated, bool busInventoryRead, IReadOnlyList<ushort?> busTypes)
    {
        if (busInventoryRead && busTypes.Count > 0 && !busTypes.Any(BusCouldReportAtaSmart))
            return SmartUnavailableReason.NoAtaDrivePresent;

        return isElevated ? SmartUnavailableReason.Unexplained : SmartUnavailableReason.NotElevated;
    }

    /// <summary>The note that goes into the report, in the words of the person reading it.</summary>
    public static string Explain(SmartUnavailableReason reason, string error) => reason switch
    {
        SmartUnavailableReason.NoAtaDrivePresent =>
            "No drive in this PC uses the ATA SMART interface, so there is no SMART attribute table " +
            $"here to read ({error}). That is the hardware, not a check that failed: NVMe SSDs and " +
            "USB-connected drives report their health through the storage reliability counters " +
            $"instead, which is what the \"{ReliabilityCheckName}\" check reads. If that check did " +
            "not read them either, its own result says so.",

        SmartUnavailableReason.NotElevated =>
            $"SMART data is not accessible on this system ({error}), and this scan is not running " +
            "with administrator rights, which is one reason Windows refuses it. The drives' " +
            "reallocated and pending sectors were NOT read; run the scan again as administrator.",

        _ =>
            $"SMART data is not accessible on this system ({error}), so reallocated and pending " +
            "sectors - the strongest warning signs there are - were NOT read. This scan did have " +
            "administrator rights, and this PC does have a drive that should be able to answer, so " +
            "the cause is something else: a storage driver that does not pass SMART through (Intel " +
            "RST and AMD RAID are the usual ones), or a controller switched to RAID mode in the " +
            "firmware. Check the drive in its maker's own tool before trusting this report on " +
            "drive health.",
    };

    /// <summary>
    /// The name of the check that DOES read NVMe and USB drives, quoted in the note above. The
    /// module that bears this name uses this same constant, so the note cannot come to quote a
    /// check that no longer exists under that name.
    /// </summary>
    public const string ReliabilityCheckName = "Drive wear & error counters";
}
