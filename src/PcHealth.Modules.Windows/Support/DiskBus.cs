using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;

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

    /// <summary>The drive Windows runs from, as "C:", or null when it was not known.</summary>
    public string? SystemDrive => _systemDrive;

    /// <summary>The number of the disk a drive letter is on, or null when no lettered partition puts it on one.</summary>
    public int? DiskOf(string? driveLetter) =>
        NormalizeLetter(driveLetter) is { } letter && _diskByLetter.TryGetValue(letter, out var number) ? number : null;

    /// <summary>The drive letters on one disk, in order ("C:", "D:"); empty when it has none.</summary>
    public IReadOnlyList<string> LettersOn(int diskNumber) =>
        [.. _diskByLetter.Where(p => p.Value == diskNumber && p.Key.Length > 0).Select(p => p.Key).Order(StringComparer.Ordinal)];

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
        if (DiskOf(letter) is not { } number || !Disks.TryGetValue(number, out var disk)) return null;
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

/// <summary>
/// Two information-only rows per attached disk (STEP-B-SPEC 2.4): where it is, and the space in use
/// on its lettered drives. The advice needs them to tell the laptop's own drive from the auditor's USB
/// drive, whose read errors are still graded Bad. Built from the drive map and the volume sizes the
/// volume check has already read, so no query is added, and tested without WMI.
///
/// A disk is named by its drive letters, or "Disk N" when it has none, never by a volume label, which
/// can be personal. The roles follow <see cref="DriveMap.ExternalBusOf"/>: the disk Windows runs from
/// is the PC's own whatever its connection (REMEDIES-PLAN D1), and an unknown bus counts as inside the
/// PC (KIT-003).
/// </summary>
public static class DiskRoles
{
    public const string SystemRole = "system", InternalRole = "internal", ExternalRole = "external";

    /// <summary>
    /// The volume check's last step: its status from the rows it measured, and only then the rows
    /// about the disks. Information-only rows must never make a check that measured no volume read
    /// as finished. Without a drive map there are no disk rows, and the status is the same: the
    /// check's note already says the map could not be read.
    /// </summary>
    public static ModuleStatus AddAfterStatus(
        List<Metric> metrics, int failures, DriveMap? map, IReadOnlyCollection<(string Letter, ulong Size, ulong Free)> measuredVolumes)
    {
        var status = metrics.Count == 0 ? ModuleStatus.Failed
            : failures == 0 ? ModuleStatus.Completed
            : ModuleStatus.PartiallyCompleted;
        if (map is not null) metrics.AddRange(From(map, measuredVolumes));
        return status;
    }

    /// <param name="map">The disks attached now, and the disk each letter is on.</param>
    /// <param name="measuredVolumes">Each lettered volume whose size and free space were read.</param>
    public static IReadOnlyList<Metric> From(DriveMap map, IReadOnlyCollection<(string Letter, ulong Size, ulong Free)> measuredVolumes)
    {
        var measured = new Dictionary<string, (ulong Size, ulong Free)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, size, free) in measuredVolumes)
        {
            if (DriveMap.NormalizeLetter(raw) is { } letter) measured.TryAdd(letter, (size, free));
        }

        var systemDisk = map.DiskOf(map.SystemDrive);
        var rows = new List<Metric>();
        foreach (var disk in map.Disks.Values.OrderBy(d => d.Number))
        {
            var letters = map.LettersOn(disk.Number);
            var role = disk.IsBoot || disk.Number == systemDisk ? SystemRole
                : DiskBus.IsExternal(disk.BusType) ? ExternalRole
                : InternalRole;
            var subject = letters.Count > 0
                ? string.Join(", ", letters)
                : "Disk " + disk.Number.ToString(CultureInfo.InvariantCulture);

            rows.Add(VolumeReadings.DiskRole(disk, subject, role, letters));
            if (UsedBytes(letters, measured) is { } used) rows.Add(VolumeReadings.DiskUsed(disk.Number, subject, used, holdsWindows: role == SystemRole));
        }
        return rows;
    }

    /// <summary>
    /// The bytes in use over a disk's lettered drives, or null unless every one of them was measured.
    /// A sum with a drive missing would read as a small number, and a disk with no lettered drive has
    /// nothing this check measured, which is not the same as nothing in use.
    /// </summary>
    private static ulong? UsedBytes(IReadOnlyList<string> letters, Dictionary<string, (ulong Size, ulong Free)> measured)
    {
        if (letters.Count == 0) return null;
        ulong used = 0;
        foreach (var letter in letters)
        {
            if (!measured.TryGetValue(letter, out var volume) || volume.Size == 0 || volume.Free > volume.Size) return null;
            used += volume.Size - volume.Free;
        }
        return used;
    }
}
