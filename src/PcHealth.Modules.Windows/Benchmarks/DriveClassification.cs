namespace PcHealth.Modules.Windows.Benchmarks;

public enum DriveClass
{
    /// <summary>Spinning disk. Fast in long transfers, very slow in scattered ones — by design.</summary>
    Hdd,

    /// <summary>Solid state on a SATA link, which caps out around 550 MB/s.</summary>
    SataSsd,

    /// <summary>Solid state on PCIe. Anything from ~2,000 to ~14,000 MB/s depending on generation.</summary>
    NvmeSsd,

    /// <summary>Anything behind USB or a card reader: the enclosure sets the speed, not the drive.</summary>
    External,

    /// <summary>Not enough information to say what this is.</summary>
    Unknown,
}

/// <summary>What a drive of this kind should manage, and how to talk about it.</summary>
public sealed record DriveExpectation(double? SequentialFloorMbps, string TypicalRange, string Note);

/// <summary>
/// Works out what kind of drive is being measured, so a number can be judged against the
/// right yardstick — 120 MB/s is healthy for a hard disk and broken for an NVMe SSD.
/// </summary>
public static class DriveClassifier
{
    // MSFT_PhysicalDisk.MediaType
    private const ushort MediaHdd = 3;
    private const ushort MediaSsd = 4;

    // MSFT_PhysicalDisk.BusType
    private const ushort BusUsb = 7;
    private const ushort BusSd = 12;
    private const ushort BusMmc = 13;
    private const ushort BusNvme = 17;

    /// <summary>
    /// Bus type is checked BEFORE media type, and that ordering is not cosmetic: USB
    /// enclosures routinely misreport what is inside them. On the machine this was written
    /// on, a 2.5" spinning drive in a USB caddy reports MediaType 4 — "SSD" — which would
    /// have had the app grade a perfectly healthy 100 MB/s hard disk against a 450 MB/s SSD
    /// floor and call it failing.
    /// </summary>
    public static DriveClass Classify(ushort? mediaType, ushort? busType)
    {
        if (busType is BusUsb or BusSd or BusMmc) return DriveClass.External;

        return (mediaType, busType) switch
        {
            (MediaHdd, _) => DriveClass.Hdd,
            (MediaSsd, BusNvme) => DriveClass.NvmeSsd,
            (MediaSsd, _) => DriveClass.SataSsd,
            (_, BusNvme) => DriveClass.NvmeSsd,
            _ => DriveClass.Unknown,
        };
    }

    /// <summary>
    /// Floors are the bottom of the class, and deliberately conservative. A drive is graded
    /// against the slowest thing its class should still manage — never against the best
    /// case, because "your Gen4 drive is not hitting Gen4 speeds" is a benchmarking hobby,
    /// not a health problem. Where the app cannot know what to expect it returns no floor
    /// and the reading is reported without a verdict.
    /// </summary>
    public static DriveExpectation Expectations(DriveClass driveClass) => driveClass switch
    {
        DriveClass.Hdd => new DriveExpectation(80,
            "80–160 MB/s for a desktop hard disk (a 5400 rpm laptop drive sits at the low end)",
            "Small scattered reads being a hundred times slower than long ones is normal for a spinning " +
            "disk — the head physically moves. That is not a fault."),

        DriveClass.SataSsd => new DriveExpectation(400,
            "450–560 MB/s, which is the SATA link's ceiling rather than the drive's",
            "Every SATA SSD lands near the same number because the cable, not the flash, is the limit."),

        DriveClass.NvmeSsd => new DriveExpectation(1800,
            "2,000–3,500 MB/s (PCIe Gen3), 4,000–7,400 (Gen4), 9,000–14,900 (Gen5)",
            "This app cannot tell which PCIe generation your drive and its slot agreed on, so it only " +
            "checks that the drive clears the slowest NVMe class. If you know it is a Gen4 or Gen5 drive " +
            "and it reads near 3,000 MB/s, it is probably in a slower slot — worth checking the manual."),

        DriveClass.External => new DriveExpectation(null,
            "whatever the enclosure and cable allow",
            "External drives are graded on nothing here on purpose: a USB 5 Gbps enclosure caps at roughly " +
            "450 MB/s and a 10 Gbps one at roughly 1,000 MB/s no matter how fast the drive inside is. " +
            "The number worth watching is whether it changes over time."),

        _ => new DriveExpectation(null,
            "unknown for this drive",
            "Windows did not say what kind of drive this is, so there is nothing to compare against. " +
            "The reading is still recorded, and a drop from it later would still be meaningful."),
    };
}
