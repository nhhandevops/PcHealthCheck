using System.Globalization;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// Which drives are outside the PC, from the bus Windows reports for each disk. MSFT_Disk and
/// MSFT_PhysicalDisk use the same BusType numbers
/// (https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk).
///
/// The four external buses are the audit kit's <c>$script:ExternalBusTypes</c> in
/// <c>audit-common.ps1</c>, so the app and the kit never disagree about which drive is the
/// laptop's own. Every other bus, an unknown one included, counts as inside the PC: missing a
/// failing internal drive costs more than counting an external one (KIT-003).
/// </summary>
public static class DiskBus
{
    public const ushort Firewire = 4, Usb = 7, Sd = 12, Mmc = 13, Nvme = 17;

    // MSFT_PhysicalDisk.MediaType
    private const ushort MediaHdd = 3, MediaSsd = 4;

    public static bool IsExternal(ushort? busType) => busType is Firewire or Usb or Sd or Mmc;

    /// <summary>What to call a drive on an external bus, in the report's words.</summary>
    public static string Describe(ushort busType) => busType switch
    {
        Usb => "a USB drive",
        Firewire => "a FireWire drive",
        _ => "a memory card",
    };

    /// <summary>
    /// Whether an SSD is inside this PC, from each physical disk's media and bus type. TRIM is a
    /// setting only SSDs use, so a PC whose drives are all hard disks has no TRIM row to grade
    /// (REMEDIES-PLAN H7). Null when that is not known: no inventory, no internal disk listed, or
    /// an internal disk that did not say what it is. Only a definite "no" leaves the row out.
    /// External drives are ignored, because USB enclosures misreport what is inside them.
    /// </summary>
    public static bool? AnyInternalSsd(IReadOnlyCollection<(ushort? MediaType, ushort? BusType)> disks)
    {
        var internalDisks = disks.Where(d => !IsExternal(d.BusType)).ToList();
        if (internalDisks.Count == 0) return null;
        if (internalDisks.Any(d => d.MediaType == MediaSsd || d.BusType == Nvme)) return true;
        return internalDisks.All(d => d.MediaType == MediaHdd) ? false : null;
    }
}

/// <summary>One disk attached now, as MSFT_Disk lists it.</summary>
public sealed record AttachedDisk(int Number, ushort? BusType, bool IsBoot);

/// <summary>
/// The disks attached now, and the disk each drive letter is on. Built from MSFT_Disk and
/// MSFT_Partition, which answer without administrator rights.
/// </summary>
public sealed class DriveMap(
    IEnumerable<AttachedDisk> disks, IEnumerable<KeyValuePair<string, int>> diskByLetter, string? systemDrive)
{
    private readonly Dictionary<string, int> _diskByLetter =
        new(diskByLetter.Select(p => KeyValuePair.Create(NormalizeLetter(p.Key) ?? "", p.Value)), StringComparer.OrdinalIgnoreCase);

    private readonly string? _systemDrive = NormalizeLetter(systemDrive);

    public IReadOnlyDictionary<int, AttachedDisk> Disks { get; } =
        disks.GroupBy(d => d.Number).ToDictionary(g => g.Key, g => g.First());

    /// <summary>
    /// The external bus a lettered volume sits on, or null when it counts as this PC's own: it is
    /// on an internal or unknown bus, it is the drive Windows runs from or on that drive's disk,
    /// or its disk is not known. The drive Windows runs from is always graded, whatever its
    /// connection (REMEDIES-PLAN D1).
    /// </summary>
    public ushort? ExternalBusOf(string? driveLetter)
    {
        var letter = NormalizeLetter(driveLetter);
        if (letter is null || string.Equals(letter, _systemDrive, StringComparison.OrdinalIgnoreCase)) return null;
        if (!_diskByLetter.TryGetValue(letter, out var number) || !Disks.TryGetValue(number, out var disk)) return null;
        if (disk.IsBoot || !DiskBus.IsExternal(disk.BusType)) return null;
        return disk.BusType;
    }

    /// <summary>
    /// Reads the disks attached now. Null, with the reason, when Windows would not list them.
    /// </summary>
    internal static List<AttachedDisk>? ReadDisks(out string? error)
    {
        var rows = Wmi.TryQuery(@"root\Microsoft\Windows\Storage", "SELECT Number, BusType, IsBoot FROM MSFT_Disk", out error);
        if (error is not null) return null;
        var disks = new List<AttachedDisk>();
        foreach (var row in rows)
        {
            if (Wmi.Get<uint>(row, "Number") is not { } number) continue;
            disks.Add(new AttachedDisk((int)number, Wmi.Get<ushort>(row, "BusType"), Wmi.Get<bool>(row, "IsBoot") ?? false));
        }
        return disks;
    }

    /// <summary>The disks and every lettered partition. Null, with the reason, when either list could not be read.</summary>
    internal static DriveMap? Read(out string? error)
    {
        var disks = ReadDisks(out error);
        if (disks is null) return null;
        var partitions = Wmi.TryQuery(@"root\Microsoft\Windows\Storage", "SELECT DriveLetter, DiskNumber FROM MSFT_Partition", out error);
        if (error is not null) return null;

        var letters = new List<KeyValuePair<string, int>>();
        foreach (var row in partitions)
        {
            var letter = NormalizeLetter(Wmi.GetString(row, "DriveLetter"));
            if (letter is null || Wmi.Get<uint>(row, "DiskNumber") is not { } number) continue;
            letters.Add(KeyValuePair.Create(letter, (int)number));
        }
        return new DriveMap(disks, letters, Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
    }

    /// <summary>"C", "c:", "C:\" and WMI's NUL for a volume with no letter all end up as "C:" or nothing.</summary>
    internal static string? NormalizeLetter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var letter = raw.Trim().TrimEnd(':', '\\', '\0');
        return letter.Length == 1 && char.IsLetter(letter[0]) ? letter.ToUpper(CultureInfo.InvariantCulture) + ":" : null;
    }
}
